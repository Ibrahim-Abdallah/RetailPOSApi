using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.DTOs.Shifts;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed class CashierShiftTests : IClassFixture<RetailApiFactory>
{
    readonly RetailApiFactory factory;
    public CashierShiftTests(RetailApiFactory factory) => this.factory = factory;

    [Theory]
    [InlineData("{\"registerId\":1}")]
    [InlineData("{\"registerId\":1,\"openingFloat\":null}")]
    [InlineData("{}")]
    [InlineData("{")]
    public async Task Open_requires_explicit_non_null_opening_float(string json)
    {
        var user = await User();
        using var client = await Auth(user.Email);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/cashier/shifts/open", content)).StatusCode);
    }

    [Fact]
    public async Task Open_accepts_explicit_zero_and_decimal_18_2_maximum()
    {
        var zeroUser = await User();
        var maxUser = await User();
        using var zeroClient = await Auth(zeroUser.Email);
        using var maxClient = await Auth(maxUser.Email);
        Assert.Equal(HttpStatusCode.Created, (await zeroClient.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest((await Register()).Id, 0))).StatusCode);
        var response = await maxClient.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest((await Register()).Id, 9_999_999_999_999_999.99m));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(9_999_999_999_999_999.99m, (await response.Content.ReadFromJsonAsync<ShiftResponse>())!.OpeningFloat);
    }

    [Theory]
    [InlineData("/api/cashier/shifts?sortDirection=")]
    [InlineData("/api/cashier/shifts?sortBy=")]
    [InlineData("/api/cashier/shifts?sortDirection=sideways")]
    [InlineData("/api/cashier/shifts?sortBy=id")]
    [InlineData("/api/management/shifts?sortDirection=")]
    [InlineData("/api/management/shifts?sortBy=")]
    [InlineData("/api/management/shifts?sortDirection=sideways")]
    [InlineData("/api/management/shifts?sortBy=id")]
    public async Task Invalid_or_empty_sort_values_return_400(string route)
    {
        using var client = await Auth(route.Contains("management") ? "manager@example.com" : "cashier@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route)).StatusCode);
    }

    [Fact]
    public async Task Open_derives_context_rounds_money_and_sets_open_lifecycle()
    {
        var user = await User();
        var register = await Register();
        using var client = await Auth(user.Email);

        var response = await client.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(register.Id, 100.005m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var shift = (await response.Content.ReadFromJsonAsync<ShiftResponse>())!;
        Assert.Equal(user.Id, shift.CashierUserId);
        Assert.Equal(register.Id, shift.RegisterId);
        Assert.Equal(register.BranchId, shift.BranchId);
        Assert.Equal(100.01m, shift.OpeningFloat);
        Assert.Equal(CashierShiftStatus.Open, shift.Status);
        Assert.Null(shift.ClosedAtUtc);
        Assert.Equal(shift.OpenedAtUtc, shift.CreatedAtUtc);
        Assert.Equal(shift.CreatedAtUtc, shift.UpdatedAtUtc);
        await WithDb(async db =>
        {
            var persisted = await db.CashierShifts.SingleAsync(x => x.Id == shift.Id);
            Assert.Null(persisted.DeclaredCash);
            Assert.Null(persisted.ExpectedCash);
            Assert.Null(persisted.CashVariance);
        });
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("10000000000000000")]
    public async Task Open_rejects_invalid_money(string value)
    {
        var user = await User();
        var register = await Register();
        using var client = await Auth(user.Email);
        using var body = JsonContent.Create(new { registerId = register.Id, openingFloat = decimal.Parse(value) });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/cashier/shifts/open", body)).StatusCode);
    }

    [Fact]
    public async Task Open_accepts_zero_and_rejects_missing_inactive_register_or_branch()
    {
        var user = await User();
        using var client = await Auth(user.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(int.MaxValue, 0))).StatusCode);
        var inactiveRegister = await Register(registerActive: false);
        var registerResponse = await client.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(inactiveRegister.Id, 0));
        Assert.Equal(HttpStatusCode.Conflict, registerResponse.StatusCode);
        Assert.Equal("Register is inactive.", (await registerResponse.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        var inactiveBranch = await Register(branchActive: false);
        var branchResponse = await client.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(inactiveBranch.Id, 0));
        Assert.Equal(HttpStatusCode.Conflict, branchResponse.StatusCode);
        Assert.Equal("Branch is inactive.", (await branchResponse.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        var active = await Register();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(active.Id, 0))).StatusCode);
    }

    [Fact]
    public async Task Open_rechecks_current_persisted_employee_state()
    {
        var user = await User();
        var register = await Register();
        using var client = await Auth(user.Email);
        await WithDb(async db =>
        {
            var persisted = await db.Users.FindAsync(user.Id);
            persisted!.IsActive = false;
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(register.Id, 10))).StatusCode);
        await WithDb(async db => Assert.False(await db.CashierShifts.AnyAsync(x => x.CashierUserId == user.Id)));
    }

    [Fact]
    public async Task Open_rechecks_current_persisted_employee_role()
    {
        var user = await User();
        var register = await Register();
        using var client = await Auth(user.Email);
        await WithDb(async db =>
        {
            var persisted = await db.Users.FindAsync(user.Id);
            persisted!.Role = UserRole.Manager;
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(register.Id, 10))).StatusCode);
        await WithDb(async db => Assert.False(await db.CashierShifts.AnyAsync(x => x.CashierUserId == user.Id)));
    }

    [Fact]
    public async Task Problem_details_are_safe_for_current_foreign_missing_and_conflict_cases()
    {
        var owner = await User();
        var other = await User();
        using var ownerClient = await Auth(owner.Email);
        using var otherClient = await Auth(other.Email);
        var none = await ownerClient.GetAsync("/api/cashier/shifts/current");
        Assert.Equal("No open cashier shift found.", (await none.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        var missingRegister = await ownerClient.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(int.MaxValue, 1));
        Assert.Equal("Register not found.", (await missingRegister.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        var register = await Register();
        var opened = await ownerClient.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(register.Id, 1));
        var shift = (await opened.Content.ReadFromJsonAsync<ShiftResponse>())!;
        var foreign = await otherClient.GetAsync($"/api/cashier/shifts/{shift.Id}");
        Assert.Equal("Cashier shift not found.", (await foreign.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        var duplicateCashier = await ownerClient.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest((await Register()).Id, 1));
        Assert.Equal("Cashier already has an open shift.", (await duplicateCashier.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        var registerConflict = await otherClient.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(register.Id, 1));
        Assert.Equal("Register already has an open shift.", (await registerConflict.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        using var manager = await Auth("manager@example.com");
        var missing = await manager.GetAsync("/api/management/shifts/2147483647");
        Assert.Equal("Cashier shift not found.", (await missing.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Own_history_filters_paginates_and_sorts_multiple_owned_rows_only()
    {
        var owner = await User();
        var foreign = await User();
        var registers = new[] { await Register(), await Register(), await Register(), await Register(), await Register(), await Register() };
        var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var owned = new[]
        {
            await Shift(owner, registers[0], CashierShiftStatus.Closed, origin.AddHours(4), origin.AddHours(3), 40),
            await Shift(owner, registers[1], CashierShiftStatus.Closed, origin.AddHours(1), origin.AddHours(5), 10),
            await Shift(owner, registers[2], CashierShiftStatus.Open, origin.AddHours(3), origin.AddHours(1), 30),
            await Shift(owner, registers[3], CashierShiftStatus.Closed, origin.AddHours(2), origin.AddHours(2), 20),
            await Shift(owner, registers[4], CashierShiftStatus.Closed, origin.AddHours(2), origin.AddHours(2), 50)
        };
        _ = await Shift(foreign, registers[5], CashierShiftStatus.Open, origin, origin, 1);
        using var client = await Auth(owner.Email);

        var open = await client.GetFromJsonAsync<PagedResponse<ShiftResponse>>("/api/cashier/shifts?status=Open&pageSize=100");
        Assert.Single(open!.Items);
        Assert.Equal(owned[2].Id, open.Items[0].Id);
        var closed = await client.GetFromJsonAsync<PagedResponse<ShiftResponse>>("/api/cashier/shifts?status=Closed&pageSize=100");
        Assert.Equal(4, closed!.TotalCount);
        Assert.All(closed.Items, x => Assert.Equal(CashierShiftStatus.Closed, x.Status));
        var first = await client.GetFromJsonAsync<PagedResponse<ShiftResponse>>("/api/cashier/shifts?page=1&pageSize=2&sortBy=openedAt&sortDirection=asc");
        var second = await client.GetFromJsonAsync<PagedResponse<ShiftResponse>>("/api/cashier/shifts?page=2&pageSize=2&sortBy=openedAt&sortDirection=asc");
        var third = await client.GetFromJsonAsync<PagedResponse<ShiftResponse>>("/api/cashier/shifts?page=3&pageSize=2&sortBy=openedAt&sortDirection=asc");
        Assert.Equal(5, first!.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(5, first.Items.Concat(second!.Items).Concat(third!.Items).Select(x => x.Id).Distinct().Count());
        Assert.All(first.Items.Concat(second.Items).Concat(third.Items), x => Assert.Equal(owner.Id, x.CashierUserId));

        foreach (var (sortBy, direction, expected) in new[]
        {
            ("openedAt", "asc", owned.OrderBy(x => x.OpenedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray()),
            ("openedAt", "desc", owned.OrderByDescending(x => x.OpenedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray()),
            ("createdAt", "asc", owned.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray()),
            ("createdAt", "desc", owned.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray())
        })
        {
            var page = await client.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/cashier/shifts?pageSize=100&sortBy={sortBy}&sortDirection={direction}");
            Assert.Equal(expected, page!.Items.Select(x => x.Id));
        }
    }

    [Fact]
    public async Task Current_history_and_detail_enforce_identity_and_validation()
    {
        var owner = await User();
        var other = await User();
        var register = await Register();
        using var ownerClient = await Auth(owner.Email);
        using var otherClient = await Auth(other.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerClient.GetAsync("/api/cashier/shifts/current")).StatusCode);
        var opened = await ownerClient.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(register.Id, 5));
        var shift = (await opened.Content.ReadFromJsonAsync<ShiftResponse>())!;
        Assert.Equal(shift.Id, (await ownerClient.GetFromJsonAsync<ShiftResponse>("/api/cashier/shifts/current"))!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/cashier/shifts/{shift.Id}")).StatusCode);
        var page = await ownerClient.GetFromJsonAsync<PagedResponse<ShiftResponse>>("/api/cashier/shifts?status=Open&page=1&pageSize=1&sortBy=createdAt&sortDirection=asc");
        Assert.Single(page!.Items);
        Assert.All(page.Items, x => Assert.Equal(owner.Id, x.CashierUserId));
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.GetAsync("/api/cashier/shifts?page=0&pageSize=101&sortBy=id&sortDirection=sideways")).StatusCode);
    }

    [Fact]
    public async Task Authorization_rules_match_cashier_and_management_surfaces()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/cashier/shifts/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/management/shifts")).StatusCode);
        foreach (var email in new[] { "admin@example.com", "manager@example.com" })
        {
            using var management = await Auth(email);
            Assert.Equal(HttpStatusCode.Forbidden, (await management.GetAsync("/api/cashier/shifts")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await management.GetAsync("/api/management/shifts")).StatusCode);
        }
        using var cashier = await Auth("cashier@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/management/shifts")).StatusCode);
    }

    [Fact]
    public async Task Management_filters_paginates_sorts_and_gets_items()
    {
        var firstUser = await User();
        var secondUser = await User();
        var firstRegister = await Register();
        var secondRegister = await RegisterInBranch(firstRegister.BranchId);
        var origin = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var shifts = new[]
        {
            await Shift(firstUser, firstRegister, CashierShiftStatus.Open, origin.AddHours(4), origin.AddHours(1), 20),
            await Shift(secondUser, secondRegister, CashierShiftStatus.Open, origin.AddHours(1), origin.AddHours(4), 10),
            await Shift(firstUser, secondRegister, CashierShiftStatus.Closed, origin.AddHours(3), origin.AddHours(2), 20),
            await Shift(secondUser, firstRegister, CashierShiftStatus.Closed, origin.AddHours(2), origin.AddHours(3), 30)
        };
        using var manager = await Auth("manager@example.com");
        using var admin = await Auth("admin@example.com");
        var branch = await manager.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/management/shifts?branchId={firstRegister.BranchId}&pageSize=100");
        Assert.Equal(4, branch!.TotalCount);
        var status = await manager.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/management/shifts?branchId={firstRegister.BranchId}&status=Open&pageSize=100");
        Assert.Equal(2, status!.TotalCount);
        Assert.All(status.Items, x => Assert.Equal(CashierShiftStatus.Open, x.Status));
        var register = await manager.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/management/shifts?registerId={firstRegister.Id}&pageSize=100");
        Assert.Equal(shifts.Where(x => x.RegisterId == firstRegister.Id).Select(x => x.Id).Order(), register!.Items.Select(x => x.Id).Order());
        var cashier = await manager.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/management/shifts?cashierUserId={firstUser.Id}&pageSize=100");
        Assert.Equal(shifts.Where(x => x.CashierUserId == firstUser.Id).Select(x => x.Id).Order(), cashier!.Items.Select(x => x.Id).Order());
        var page1 = await manager.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/management/shifts?branchId={firstRegister.BranchId}&page=1&pageSize=2&sortBy=openedAt&sortDirection=asc");
        var page2 = await manager.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/management/shifts?branchId={firstRegister.BranchId}&page=2&pageSize=2&sortBy=openedAt&sortDirection=asc");
        Assert.Equal(2, page1!.TotalPages);
        Assert.Equal(4, page1.Items.Concat(page2!.Items).Select(x => x.Id).Distinct().Count());
        foreach (var (sortBy, direction, expected) in new[]
        {
            ("openedAt", "asc", shifts.OrderBy(x => x.OpenedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray()),
            ("openedAt", "desc", shifts.OrderByDescending(x => x.OpenedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray()),
            ("createdAt", "asc", shifts.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray()),
            ("createdAt", "desc", shifts.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArray()),
            ("openingFloat", "asc", shifts.OrderBy(x => x.OpeningFloat).ThenBy(x => x.Id).Select(x => x.Id).ToArray()),
            ("openingFloat", "desc", shifts.OrderByDescending(x => x.OpeningFloat).ThenBy(x => x.Id).Select(x => x.Id).ToArray())
        })
        {
            var sorted = await manager.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/management/shifts?branchId={firstRegister.BranchId}&pageSize=100&sortBy={sortBy}&sortDirection={direction}");
            Assert.Equal(expected, sorted!.Items.Select(x => x.Id));
        }
        Assert.Equal(shifts[0].Id, (await admin.GetFromJsonAsync<ShiftResponse>($"/api/management/shifts/{shifts[0].Id}"))!.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/management/shifts?branchId=0&registerId=-1&cashierUserId=0&status=999&sortBy=id")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_same_cashier_different_registers_allows_exactly_one_open_shift()
    {
        var user = await User();
        var first = await Register();
        var second = await Register();
        using var client1 = await Auth(user.Email);
        using var client2 = await Auth(user.Email);
        var coordinator = factory.Services.GetRequiredService<CashierShiftSaveCoordinator>();
        coordinator.Enable();
        try
        {
            var responses = await Task.WhenAll(
                client1.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(first.Id, 1)),
                client2.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(second.Id, 1)));
            Assert.Equal(2, coordinator.Arrivals);
            Assert.Equal(1, coordinator.DatabaseFailures);
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order().ToArray());
            await WithDb(async db => Assert.Equal(1, await db.CashierShifts.CountAsync(x => x.CashierUserId == user.Id && x.Status == CashierShiftStatus.Open)));
        }
        finally { coordinator.Disable(); }
    }

    [Fact]
    public async Task Concurrent_different_cashiers_same_register_allows_exactly_one_open_shift()
    {
        var first = await User();
        var second = await User();
        var register = await Register();
        using var client1 = await Auth(first.Email);
        using var client2 = await Auth(second.Email);
        var coordinator = factory.Services.GetRequiredService<CashierShiftSaveCoordinator>();
        coordinator.Enable();
        try
        {
            var responses = await Task.WhenAll(
                client1.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(register.Id, 1)),
                client2.PostAsJsonAsync("/api/cashier/shifts/open", new OpenShiftRequest(register.Id, 1)));
            Assert.Equal(2, coordinator.Arrivals);
            Assert.Equal(1, coordinator.DatabaseFailures);
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order().ToArray());
            await WithDb(async db => Assert.Equal(1, await db.CashierShifts.CountAsync(x => x.RegisterId == register.Id && x.Status == CashierShiftStatus.Open)));
        }
        finally { coordinator.Disable(); }
    }

    [Fact]
    public async Task Relational_database_rejects_duplicate_open_cashier_and_register_rows()
    {
        var firstUser = await User();
        var secondUser = await User();
        var firstRegister = await Register();
        var secondRegister = await Register();
        var now = DateTimeOffset.UtcNow;
        await Shift(firstUser, firstRegister, CashierShiftStatus.Open, now, now, 1);
        await WithDb(async db =>
        {
            db.CashierShifts.Add(NewOpen(firstUser.Id, secondRegister, now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
        await WithDb(async db =>
        {
            db.CashierShifts.Add(NewOpen(secondUser.Id, firstRegister, now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    async Task<User> User()
    {
        User? result = null;
        await WithDb(async db =>
        {
            using var scope = factory.Services.CreateScope();
            var email = $"cashier-{Guid.NewGuid():N}@example.com";
            var now = DateTimeOffset.UtcNow;
            result = new User { FirstName = "Test", LastName = "Cashier", Email = email, NormalizedEmail = EmailNormalizer.Normalize(email), PasswordHash = "", Role = UserRole.Cashier, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            result.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(result, "Valid1!Password");
            db.Users.Add(result);
            await db.SaveChangesAsync();
        });
        return result!;
    }

    async Task<Register> Register(bool registerActive = true, bool branchActive = true)
    {
        Register? result = null;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var branch = new Branch { Name = "Test Branch", Code = Guid.NewGuid().ToString("N"), Address = "Address", IsActive = branchActive, CreatedAtUtc = now, UpdatedAtUtc = now };
            result = new Register { Branch = branch, Name = "Test Register", Code = Guid.NewGuid().ToString("N"), IsActive = registerActive, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(result);
            await db.SaveChangesAsync();
        });
        return result!;
    }

    async Task<Register> RegisterInBranch(int branchId)
    {
        Register? result = null;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            result = new Register { BranchId = branchId, Name = "Test Register", Code = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(result);
            await db.SaveChangesAsync();
        });
        return result!;
    }

    async Task<CashierShift> Shift(User user, Register register, CashierShiftStatus status,
        DateTimeOffset openedAt, DateTimeOffset createdAt, decimal openingFloat)
    {
        CashierShift? result = null;
        await WithDb(async db =>
        {
            result = new CashierShift
            {
                BranchId = register.BranchId,
                RegisterId = register.Id,
                CashierUserId = user.Id,
                Status = status,
                OpeningFloat = openingFloat,
                OpenedAtUtc = openedAt,
                ClosedAtUtc = status == CashierShiftStatus.Closed ? openedAt.AddHours(8) : null,
                DeclaredCash = status == CashierShiftStatus.Closed ? openingFloat : null,
                ExpectedCash = status == CashierShiftStatus.Closed ? openingFloat : null,
                CashVariance = status == CashierShiftStatus.Closed ? 0 : null,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = status == CashierShiftStatus.Closed ? openedAt.AddHours(8) : createdAt
            };
            db.CashierShifts.Add(result);
            await db.SaveChangesAsync();
        });
        return result!;
    }

    static CashierShift NewOpen(int userId, Register register, DateTimeOffset now) => new()
    {
        BranchId = register.BranchId,
        RegisterId = register.Id,
        CashierUserId = userId,
        Status = CashierShiftStatus.Open,
        OpeningFloat = 1,
        OpenedAtUtc = now,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    async Task<HttpClient> Auth(string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Valid1!Password"));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        return client;
    }

    async Task WithDb(Func<AppDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
