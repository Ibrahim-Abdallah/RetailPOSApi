using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.DTOs.Shifts;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed class ShiftClosingTests : IClassFixture<RetailApiFactory>
{
    readonly RetailApiFactory factory;
    public ShiftClosingTests(RetailApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Close_requires_cashier_authentication_and_role()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/cashier/shifts/1/close", new CloseShiftRequest(0))).StatusCode);
        foreach (var email in new[] { "admin@example.com", "manager@example.com" })
        {
            using var client = await Auth(email);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/api/cashier/shifts/1/close", new CloseShiftRequest(0))).StatusCode);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"declaredCash\":null}")]
    [InlineData("{\"declaredCash\":-0.01}")]
    public async Task Close_rejects_missing_null_or_negative_declared_cash(string json)
    {
        var (user, shift) = await OpenShift(10);
        using var client = await Auth(user.Email);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync($"/api/cashier/shifts/{shift.Id}/close", content)).StatusCode);
    }

    [Fact]
    public async Task Close_enforces_ownership_and_active_cashier_state()
    {
        var (owner, shift) = await OpenShift(10);
        var other = await User();
        using var otherClient = await Auth(other.Email);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(10))).StatusCode);

        using var ownerClient = await Auth(owner.Email);
        await WithDb(async db => { (await db.Users.FindAsync(owner.Id))!.IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ownerClient.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(10))).StatusCode);
    }

    [Fact]
    public async Task Opening_float_only_closes_and_persists_rounded_snapshot_without_mutating_identity()
    {
        var before = DateTimeOffset.UtcNow;
        var (user, shift) = await OpenShift(100.01m);
        using var client = await Auth(user.Email);
        var response = await client.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(99.995m));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var closed = (await response.Content.ReadFromJsonAsync<ShiftResponse>())!;
        Assert.Equal(CashierShiftStatus.Closed, closed.Status);
        Assert.Equal(100m, closed.DeclaredCash);
        Assert.Equal(100.01m, closed.ExpectedCash);
        Assert.Equal(-0.01m, closed.CashVariance);
        Assert.NotNull(closed.ClosedAtUtc);
        Assert.InRange(closed.ClosedAtUtc!.Value, before, DateTimeOffset.UtcNow);
        Assert.Equal(shift.BranchId, closed.BranchId);
        Assert.Equal(shift.RegisterId, closed.RegisterId);
        Assert.Equal(user.Id, closed.CashierUserId);
        Assert.Equal(shift.OpeningFloat, closed.OpeningFloat);
    }

    [Fact]
    public async Task Second_close_conflicts_and_never_overwrites_snapshot()
    {
        var (user, shift) = await OpenShift(25);
        using var client = await Auth(user.Email);
        var first = await client.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(30));
        var original = (await first.Content.ReadFromJsonAsync<ShiftResponse>())!;
        var second = await client.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(999));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var stored = await ReadShift(shift.Id);
        Assert.Equal(original.ClosedAtUtc, stored.ClosedAtUtc);
        Assert.Equal(30m, stored.DeclaredCash);
        Assert.Equal(25m, stored.ExpectedCash);
        Assert.Equal(5m, stored.CashVariance);
    }

    [Fact]
    public async Task Open_sale_conflict_rolls_back_every_closing_field()
    {
        var (user, shift) = await OpenShift(20);
        await AddSale(shift, SaleStatus.Open);
        using var client = await Auth(user.Email);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(20))).StatusCode);
        var stored = await ReadShift(shift.Id);
        Assert.Equal(CashierShiftStatus.Open, stored.Status);
        Assert.Null(stored.ClosedAtUtc);
        Assert.Null(stored.DeclaredCash);
        Assert.Null(stored.ExpectedCash);
        Assert.Null(stored.CashVariance);
    }

    [Fact]
    public async Task Reconciliation_uses_amount_applied_and_only_cash_across_split_and_multiple_payments()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.Completed);
        await AddPayment(sale, PaymentMethod.Cash, 22.88m, 30m);
        await AddPayment(sale, PaymentMethod.Cash, 6.42m, 10m);
        await AddPayment(sale, PaymentMethod.Card, 5m, 5m);
        await AddPayment(sale, PaymentMethod.Other, 2m, 2m);
        var closed = await Close(user, shift, 129.30m);
        Assert.Equal(129.30m, closed.ExpectedCash);
        Assert.Equal(0m, closed.CashVariance);
    }

    [Fact]
    public async Task Completed_cash_refunds_aggregate_while_card_other_and_failed_refunds_are_excluded()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.PartiallyRefunded);
        var cash = await AddPayment(sale, PaymentMethod.Cash, 40, 40);
        var card = await AddPayment(sale, PaymentMethod.Card, 10, 10);
        await AddRefund(sale, RefundStatus.Completed, (cash, PaymentMethod.Cash, 3), (cash, PaymentMethod.Cash, 7), (card, PaymentMethod.Card, 4));
        await AddRefund(sale, RefundStatus.Completed, (card, PaymentMethod.Other, 2));
        await AddRefund(sale, RefundStatus.Failed, (cash, PaymentMethod.Cash, 9));
        var closed = await Close(user, shift, 130);
        Assert.Equal(130m, closed.ExpectedCash);
    }

    [Theory]
    [InlineData(SaleStatus.Refunded, 22.88, 22.88, 100)]
    [InlineData(SaleStatus.PartiallyRefunded, 40, 10, 130)]
    public async Task Refunded_sales_keep_original_cash_then_subtract_refund_once(
        SaleStatus status, decimal originalCash, decimal refundCash, decimal expected)
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, status);
        var payment = await AddPayment(sale, PaymentMethod.Cash, originalCash, originalCash);
        await AddRefund(sale, RefundStatus.Completed, (payment, PaymentMethod.Cash, refundCash));
        Assert.Equal(expected, (await Close(user, shift, expected)).ExpectedCash);
    }

    [Fact]
    public async Task Voided_sale_removes_only_its_original_cash_effect_once()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.Voided);
        await AddPayment(sale, PaymentMethod.Cash, 15, 20);
        await AddPayment(sale, PaymentMethod.Card, 8, 8);
        await AddPayment(sale, PaymentMethod.Other, 2, 2);
        Assert.Equal(100m, (await Close(user, shift, 100)).ExpectedCash);
    }

    [Fact]
    public async Task Closed_values_are_exposed_by_cashier_and_management_reads_while_open_values_are_null()
    {
        var (user, shift) = await OpenShift(12);
        using var cashier = await Auth(user.Email);
        var open = await cashier.GetFromJsonAsync<ShiftResponse>($"/api/cashier/shifts/{shift.Id}");
        Assert.Null(open!.DeclaredCash); Assert.Null(open.ExpectedCash); Assert.Null(open.CashVariance);
        await Close(user, shift, 15);
        var own = await cashier.GetFromJsonAsync<ShiftResponse>($"/api/cashier/shifts/{shift.Id}");
        using var manager = await Auth("manager@example.com");
        var management = await manager.GetFromJsonAsync<ShiftResponse>($"/api/management/shifts/{shift.Id}");
        Assert.Equal(15m, own!.DeclaredCash); Assert.Equal(12m, own.ExpectedCash); Assert.Equal(3m, own.CashVariance);
        Assert.Equal(own.DeclaredCash, management!.DeclaredCash);
        Assert.Equal(own.ExpectedCash, management.ExpectedCash);
        Assert.Equal(own.CashVariance, management.CashVariance);
    }

    [Fact]
    public async Task Competing_close_requests_have_exactly_one_winner()
    {
        var (user, shift) = await OpenShift(10);
        using var first = await Auth(user.Email);
        using var second = await Auth(user.Email);
        var responses = await Task.WhenAll(
            first.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(10)),
            second.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(99)));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order().ToArray());
        var stored = await ReadShift(shift.Id);
        Assert.Contains(stored.DeclaredCash, new decimal?[] { 10m, 99m });
        Assert.Equal(stored.DeclaredCash - 10m, stored.CashVariance);
    }

    [Fact]
    public async Task Explicit_zero_declared_cash_is_valid_and_produces_a_shortage()
    {
        var (user, shift) = await OpenShift(10);
        var closed = await Close(user, shift, 0);
        Assert.Equal(0m, closed.DeclaredCash);
        Assert.Equal(10m, closed.ExpectedCash);
        Assert.Equal(-10m, closed.CashVariance);
    }

    [Fact]
    public async Task Closing_timestamps_come_from_TimeProvider()
    {
        var instant = DateTimeOffset.UtcNow;
        var (user, shift) = await OpenShift(10);
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(instant));
        }));
        using var client = configured.CreateClient();
        await Authenticate(client, user.Email);
        var response = await client.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(10));
        response.EnsureSuccessStatusCode();
        var closed = (await response.Content.ReadFromJsonAsync<ShiftResponse>())!;
        Assert.Equal(instant, closed.ClosedAtUtc);
        Assert.Equal(instant, closed.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(SaleStatus.Completed)]
    [InlineData(SaleStatus.PartiallyRefunded)]
    [InlineData(SaleStatus.Refunded)]
    public async Task Original_completed_cash_is_included_for_each_non_void_historical_sale_status(SaleStatus status)
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, status);
        await AddPayment(sale, PaymentMethod.Cash, 25, 25);
        Assert.Equal(125m, (await Close(user, shift, 125)).ExpectedCash);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Refunded)]
    public async Task Non_completed_cash_payment_status_is_excluded(PaymentStatus paymentStatus)
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.Completed);
        await AddPayment(sale, PaymentMethod.Cash, 25, 30, paymentStatus);
        Assert.Equal(100m, (await Close(user, shift, 100)).ExpectedCash);
    }

    [Theory]
    [InlineData(PaymentMethod.Card)]
    [InlineData(PaymentMethod.Other)]
    public async Task Non_cash_sale_payment_is_excluded(PaymentMethod method)
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.Completed);
        await AddPayment(sale, method, 25, 25);
        Assert.Equal(100m, (await Close(user, shift, 100)).ExpectedCash);
    }

    [Fact]
    public async Task Split_cash_and_card_sale_counts_only_cash_portion()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.Completed);
        await AddPayment(sale, PaymentMethod.Cash, 6.42m, 10);
        await AddPayment(sale, PaymentMethod.Card, 5, 5);
        Assert.Equal(106.42m, (await Close(user, shift, 106.42m)).ExpectedCash);
    }

    [Fact]
    public async Task Repeated_completed_cash_refunds_subtract_all_partial_amounts()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.PartiallyRefunded);
        var payment = await AddPayment(sale, PaymentMethod.Cash, 40, 40);
        await AddRefund(sale, RefundStatus.Completed, (payment, PaymentMethod.Cash, 3));
        await AddRefund(sale, RefundStatus.Completed, (payment, PaymentMethod.Cash, 7));
        Assert.Equal(130m, (await Close(user, shift, 130)).ExpectedCash);
    }

    [Theory]
    [InlineData(PaymentMethod.Card)]
    [InlineData(PaymentMethod.Other)]
    public async Task Non_cash_refund_allocation_is_excluded(PaymentMethod refundMethod)
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.PartiallyRefunded);
        var payment = await AddPayment(sale, PaymentMethod.Cash, 20, 20);
        await AddRefund(sale, RefundStatus.Completed, (payment, refundMethod, 5));
        Assert.Equal(120m, (await Close(user, shift, 120)).ExpectedCash);
    }

    [Fact]
    public async Task Split_cash_and_card_refund_subtracts_only_cash_portion()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.PartiallyRefunded);
        var cash = await AddPayment(sale, PaymentMethod.Cash, 20, 20);
        var card = await AddPayment(sale, PaymentMethod.Card, 10, 10);
        await AddRefund(sale, RefundStatus.Completed,
            (cash, PaymentMethod.Cash, 3), (card, PaymentMethod.Card, 4));
        Assert.Equal(117m, (await Close(user, shift, 117)).ExpectedCash);
    }

    [Fact]
    public async Task Zero_total_refund_without_allocations_contributes_zero()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.PartiallyRefunded);
        await AddPayment(sale, PaymentMethod.Cash, 20, 20);
        await AddRefund(sale, RefundStatus.Completed);
        Assert.Equal(120m, (await Close(user, shift, 120)).ExpectedCash);
    }

    [Fact]
    public async Task Failed_cash_refund_does_not_reduce_expected_cash()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.PartiallyRefunded);
        var payment = await AddPayment(sale, PaymentMethod.Cash, 20, 20);
        await AddRefund(sale, RefundStatus.Failed, (payment, PaymentMethod.Cash, 5));
        Assert.Equal(120m, (await Close(user, shift, 120)).ExpectedCash);
    }

    [Theory]
    [InlineData(PaymentMethod.Cash, 100)]
    [InlineData(PaymentMethod.Card, 100)]
    [InlineData(PaymentMethod.Other, 100)]
    public async Task Voided_single_tender_sale_has_zero_net_cash_effect(PaymentMethod method, decimal expected)
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.Voided);
        await AddPayment(sale, method, 15, method == PaymentMethod.Cash ? 20 : 15);
        Assert.Equal(expected, (await Close(user, shift, expected)).ExpectedCash);
    }

    [Fact]
    public async Task Split_tender_void_removes_only_original_cash_portion()
    {
        var (user, shift) = await OpenShift(100);
        var voided = await AddSale(shift, SaleStatus.Voided);
        await AddPayment(voided, PaymentMethod.Cash, 15, 20);
        await AddPayment(voided, PaymentMethod.Card, 8, 8);
        var retained = await AddSale(shift, SaleStatus.Completed);
        await AddPayment(retained, PaymentMethod.Cash, 5, 5);
        Assert.Equal(105m, (await Close(user, shift, 105)).ExpectedCash);
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(105, 5)]
    [InlineData(95, -5)]
    public async Task Variance_supports_exact_overage_and_shortage(decimal declared, decimal expectedVariance)
    {
        var (user, shift) = await OpenShift(100);
        Assert.Equal(expectedVariance, (await Close(user, shift, declared)).CashVariance);
    }

    [Fact]
    public async Task Own_and_management_lists_expose_the_persisted_closing_snapshot()
    {
        var (user, shift) = await OpenShift(12);
        await Close(user, shift, 15);
        using var cashier = await Auth(user.Email);
        using var manager = await Auth("manager@example.com");
        var own = await cashier.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/cashier/shifts?pageSize=100");
        var management = await manager.GetFromJsonAsync<PagedResponse<ShiftResponse>>($"/api/management/shifts?cashierUserId={user.Id}&pageSize=100");
        var ownItem = Assert.Single(own!.Items, x => x.Id == shift.Id);
        var managementItem = Assert.Single(management!.Items, x => x.Id == shift.Id);
        Assert.Equal((15m, 12m, 3m), (ownItem.DeclaredCash, ownItem.ExpectedCash, ownItem.CashVariance));
        Assert.Equal((15m, 12m, 3m), (managementItem.DeclaredCash, managementItem.ExpectedCash, managementItem.CashVariance));
    }

    [Fact]
    public async Task Closed_snapshot_is_historical_and_is_not_recalculated_by_reads()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddSale(shift, SaleStatus.Completed);
        await AddPayment(sale, PaymentMethod.Cash, 10, 10);
        await Close(user, shift, 110);
        await AddPayment(sale, PaymentMethod.Cash, 90, 90);
        using var cashier = await Auth(user.Email);
        var read = await cashier.GetFromJsonAsync<ShiftResponse>($"/api/cashier/shifts/{shift.Id}");
        Assert.Equal(110m, read!.ExpectedCash);
        Assert.Equal(0m, read.CashVariance);
    }

    [Fact]
    public async Task Sale_creation_after_committed_close_is_rejected_and_no_open_sale_is_persisted()
    {
        var (user, shift) = await OpenShift(100);
        await Close(user, shift, 100);
        using var cashier = await Auth(user.Email);
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsync("/api/cashier/sales", null)).StatusCode);
        await WithDb(async db => Assert.False(await db.Sales.AnyAsync(x => x.CashierShiftId == shift.Id && x.Status == SaleStatus.Open)));
    }

    [Fact]
    public async Task Concurrent_close_and_sale_create_never_both_succeed()
    {
        var (user, shift) = await OpenShift(100);
        using var closing = await Auth(user.Email);
        using var creating = await Auth(user.Email);
        var closeTask = closing.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(100));
        var createTask = creating.PostAsync("/api/cashier/sales", null);
        var results = await Task.WhenAll(closeTask, createTask);
        Assert.NotEqual(new[] { HttpStatusCode.OK, HttpStatusCode.Created }, results.Select(x => x.StatusCode).ToArray());
        var stored = await ReadShift(shift.Id);
        var openSales = 0;
        await WithDb(async db => openSales = await db.Sales.CountAsync(x => x.CashierShiftId == shift.Id && x.Status == SaleStatus.Open));
        Assert.True((stored.Status == CashierShiftStatus.Closed && openSales == 0) ||
                    (stored.Status == CashierShiftStatus.Open && openSales == 1));
    }

    [Fact]
    public async Task Concurrent_close_and_sale_completion_never_commit_a_closed_shift_with_an_open_sale()
    {
        var (user, shift) = await OpenShift(100);
        var sale = await AddCompletableZeroSale(shift);
        using var closing = await Auth(user.Email);
        using var completing = await Auth(user.Email);
        var results = await Task.WhenAll(
            closing.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(100)),
            completing.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete",
                new CompleteSaleRequest(Guid.NewGuid().ToString("N"), [])));
        Assert.All(results, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        var storedShift = await ReadShift(shift.Id);
        SaleStatus storedSale = SaleStatus.Open;
        await WithDb(async db => storedSale = await db.Sales.Where(x => x.Id == sale.Id).Select(x => x.Status).SingleAsync());
        Assert.False(storedShift.Status == CashierShiftStatus.Closed && storedSale == SaleStatus.Open);
    }

    async Task<ShiftResponse> Close(User user, CashierShift shift, decimal declared)
    {
        using var client = await Auth(user.Email);
        var response = await client.PostAsJsonAsync($"/api/cashier/shifts/{shift.Id}/close", new CloseShiftRequest(declared));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ShiftResponse>())!;
    }

    async Task<(User User, CashierShift Shift)> OpenShift(decimal opening)
    {
        var user = await User();
        CashierShift? shift = null;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var branch = new Branch { Name = "Close Branch", Code = Guid.NewGuid().ToString("N"), Address = "A", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var register = new Register { Branch = branch, Name = "Close Register", Code = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            shift = new CashierShift { Branch = branch, Register = register, CashierUserId = user.Id, Status = CashierShiftStatus.Open, OpeningFloat = opening, OpenedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(shift); await db.SaveChangesAsync();
        });
        return (user, shift!);
    }

    async Task<Sale> AddSale(CashierShift shift, SaleStatus status)
    {
        Sale? result = null;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            result = new Sale { BranchId = shift.BranchId, RegisterId = shift.RegisterId, CashierShiftId = shift.Id, CashierUserId = shift.CashierUserId, Status = status, CompletedAtUtc = status == SaleStatus.Open ? null : now, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(result); await db.SaveChangesAsync();
        });
        return result!;
    }

    async Task<Sale> AddCompletableZeroSale(CashierShift shift)
    {
        Sale? result = null;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var tax = new TaxRate { Name = "Zero", Percentage = 0, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var product = new Product { Sku = Guid.NewGuid().ToString("N"), Name = "Zero", UnitPrice = 0, TaxRate = tax, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            result = new Sale
            {
                BranchId = shift.BranchId, RegisterId = shift.RegisterId, CashierShiftId = shift.Id,
                CashierUserId = shift.CashierUserId, Status = SaleStatus.Open, CreatedAtUtc = now, UpdatedAtUtc = now,
                Lines = [new SaleLine { Product = product, ProductSku = product.Sku, ProductName = product.Name,
                    Quantity = 1, UnitPrice = 0, TaxRate = tax, TaxRateName = tax.Name, TaxRatePercentage = 0 }]
            };
            db.Add(result); await db.SaveChangesAsync();
        });
        return result!;
    }

    async Task<Payment> AddPayment(Sale sale, PaymentMethod method, decimal applied, decimal tendered,
        PaymentStatus status = PaymentStatus.Completed)
    {
        Payment? result = null;
        await WithDb(async db =>
        {
            result = new Payment { SaleId = sale.Id, Method = method, AmountApplied = applied, TenderedAmount = tendered, ChangeAmount = tendered - applied, Status = status, CreatedAtUtc = DateTimeOffset.UtcNow };
            db.Add(result); await db.SaveChangesAsync();
        });
        return result!;
    }

    async Task AddRefund(Sale sale, RefundStatus status, params (Payment Original, PaymentMethod Method, decimal Amount)[] payments)
    {
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var refund = new Refund { SaleId = sale.Id, ProcessedByUserId = sale.CashierUserId, Status = status, TotalAmount = payments.Sum(x => x.Amount), Reason = "Test", CreatedAtUtc = now, UpdatedAtUtc = now };
            foreach (var item in payments) refund.Payments.Add(new RefundPayment { OriginalPaymentId = item.Original.Id, Method = item.Method, Amount = item.Amount, CreatedAtUtc = now });
            db.Add(refund); await db.SaveChangesAsync();
        });
    }

    async Task<CashierShift> ReadShift(int id)
    {
        CashierShift? result = null;
        await WithDb(async db => result = await db.CashierShifts.AsNoTracking().SingleAsync(x => x.Id == id));
        return result!;
    }

    async Task<User> User()
    {
        User? result = null;
        await WithDb(async db =>
        {
            using var scope = factory.Services.CreateScope();
            var email = $"closer-{Guid.NewGuid():N}@example.com"; var now = DateTimeOffset.UtcNow;
            result = new User { FirstName = "Shift", LastName = "Closer", Email = email, NormalizedEmail = EmailNormalizer.Normalize(email), PasswordHash = "", Role = UserRole.Cashier, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            result.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(result, "Valid1!Password");
            db.Add(result); await db.SaveChangesAsync();
        });
        return result!;
    }

    async Task<HttpClient> Auth(string email)
    {
        var client = factory.CreateClient();
        await Authenticate(client, email);
        return client;
    }

    static async Task Authenticate(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Valid1!Password"));
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
    }

    async Task WithDb(Func<AppDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
