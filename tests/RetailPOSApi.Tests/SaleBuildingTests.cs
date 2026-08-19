using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed class SaleBuildingTests : IClassFixture<RetailApiFactory>
{
    readonly RetailApiFactory factory;
    public SaleBuildingTests(RetailApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Create_derives_context_and_requires_current_open_shift()
    {
        await CloseOpenShifts("cashier@example.com");
        using var cashier = await Auth("cashier@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsync("/api/cashier/sales", null)).StatusCode);
        var (_, register, shift) = await SeedContext("cashier@example.com");
        var response = await cashier.PostAsync("/api/cashier/sales", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = (await response.Content.ReadFromJsonAsync<SaleResponse>())!;
        Assert.Equal((register.BranchId, register.Id, shift.Id), (sale.BranchId, sale.RegisterId, sale.CashierShiftId));
        Assert.Equal(SaleStatus.Open, sale.Status);
        Assert.Equal((0m, 0m, 0m, 0m), (sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.TotalAmount));
        Assert.Empty(sale.Lines);
    }

    [Fact]
    public async Task Calculations_same_product_and_discount_lifecycle_are_deterministic()
    {
        var (_, _, _) = await SeedContext("cashier@example.com");
        var (product, percentage, fixedAmount) = await SeedCatalog(100m, 14m);
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 2));
        var line = Assert.Single(sale.Lines);
        Assert.Equal((100m, 0m, 100m, 14m, 114m), (line.UnitPrice, line.UnitDiscountAmount, line.UnitNetAmount, line.UnitTaxAmount, line.UnitTotal));
        Assert.Equal((200m, 0m, 28m, 228m), (line.LineSubtotal, line.LineDiscountTotal, line.LineTaxTotal, line.LineTotal));
        Assert.Equal((200m, 0m, 28m, 228m), (sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.TotalAmount));
        await AssertTotalsInvariant(sale.Id);

        sale = await Put(cashier, $"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount", new ApplySaleLineDiscountRequest(percentage.Id));
        line = Assert.Single(sale.Lines);
        Assert.Equal((10m, 90m, 12.60m, 102.60m), (line.UnitDiscountAmount, line.UnitNetAmount, line.UnitTaxAmount, line.UnitTotal));
        Assert.Equal((200m, 20m, 25.20m, 205.20m), (line.LineSubtotal, line.LineDiscountTotal, line.LineTaxTotal, line.LineTotal));
        Assert.Equal((200m, 20m, 25.20m, 205.20m), (sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.TotalAmount));

        sale = await Put(cashier, $"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount", new ApplySaleLineDiscountRequest(fixedAmount.Id));
        Assert.Equal(0m, Assert.Single(sale.Lines).UnitTotal);
        sale = await Delete(cashier, $"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount");
        Assert.Null(Assert.Single(sale.Lines).DiscountId);
        Assert.Equal(228m, sale.TotalAmount);

        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 3));
        Assert.Single(sale.Lines);
        Assert.Equal(5, sale.Lines[0].Quantity);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Snapshot_survives_configuration_changes_and_quantity_update()
    {
        await SeedContext("cashier@example.com");
        var (product, percentage, _) = await SeedCatalog(10m, 14m);
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 1, percentage.Id));
        var before = Assert.Single(sale.Lines);
        await WithDb(async db =>
        {
            var p = await db.Products.FindAsync(product.Id); p!.Name = "Changed"; p.UnitPrice = 20;
            var t = await db.TaxRates.FindAsync(product.TaxRateId); t!.Name = "Changed tax"; t.Percentage = 20;
            var d = await db.Discounts.FindAsync(percentage.Id); d!.Name = "Changed discount"; d.Value = 50;
            await db.SaveChangesAsync();
        });
        var readBeforeMutation = (await cashier.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}"))!.Lines.Single();
        Assert.Equal((before.ProductSku, before.ProductName, before.UnitPrice, before.TaxRateId, before.TaxRateName, before.TaxRatePercentage, before.DiscountId, before.DiscountName, before.DiscountType, before.DiscountValue),
            (readBeforeMutation.ProductSku, readBeforeMutation.ProductName, readBeforeMutation.UnitPrice, readBeforeMutation.TaxRateId, readBeforeMutation.TaxRateName, readBeforeMutation.TaxRatePercentage, readBeforeMutation.DiscountId, readBeforeMutation.DiscountName, readBeforeMutation.DiscountType, readBeforeMutation.DiscountValue));
        sale = await Put(cashier, $"/api/cashier/sales/{sale.Id}/lines/{before.Id}", new UpdateSaleLineQuantityRequest(2));
        var after = Assert.Single(sale.Lines);
        Assert.Equal((before.ProductName, before.UnitPrice, before.TaxRateName, before.TaxRatePercentage, before.DiscountName, before.DiscountValue),
            (after.ProductName, after.UnitPrice, after.TaxRateName, after.TaxRatePercentage, after.DiscountName, after.DiscountValue));
        Assert.Equal(20.52m, sale.TotalAmount);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 1, percentage.Id));
        Assert.Single(sale.Lines);
        Assert.Equal(3, sale.Lines[0].Quantity);
        Assert.Equal(before.UnitPrice, sale.Lines[0].UnitPrice);
        Assert.Equal(before.TaxRatePercentage, sale.Lines[0].TaxRatePercentage);
        Assert.Equal(before.DiscountValue, sale.Lines[0].DiscountValue);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Removal_recalculates_to_zero_and_sale_stays_open()
    {
        await SeedContext("cashier@example.com");
        var (product, _, _) = await SeedCatalog(10m, 14m);
        var (otherProduct, _, _) = await SeedCatalog(5m, 0m);
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 2));
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(otherProduct.Id, 3));
        var firstLine = sale.Lines.Single(x => x.ProductId == product.Id);
        var remaining = sale.Lines.Single(x => x.ProductId == otherProduct.Id);
        sale = await Delete(cashier, $"/api/cashier/sales/{sale.Id}/lines/{firstLine.Id}");
        Assert.Single(sale.Lines);
        Assert.Equal(remaining.LineTotal, sale.TotalAmount);
        await AssertTotalsInvariant(sale.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{int.MaxValue}")).StatusCode);
        sale = await Delete(cashier, $"/api/cashier/sales/{sale.Id}/lines/{remaining.Id}");
        Assert.Empty(sale.Lines);
        Assert.Equal(SaleStatus.Open, sale.Status);
        Assert.Equal((0m, 0m, 0m, 0m), (sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.TotalAmount));
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Ownership_and_management_authorization_are_enforced()
    {
        await SeedContext("cashier@example.com");
        var (product, discount, _) = await SeedCatalog(10m, 14m);
        using var owner = await Auth("cashier@example.com");
        var sale = await CreateSale(owner);
        sale = await Post<AddSaleLineRequest>(owner, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 1));
        var ownerLine = sale.Lines.Single();
        var secondOwnerSale = await CreateSale(owner);
        secondOwnerSale = await Post<AddSaleLineRequest>(owner, $"/api/cashier/sales/{secondOwnerSale.Id}/lines", new(product.Id, 1, discount.Id));
        var other = await CreateCashier();
        await SeedContext(other.Email);
        using var otherClient = await Auth(other.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/cashier/sales/{sale.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(1, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{ownerLine.Id}", new UpdateSaleLineQuantityRequest(2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{ownerLine.Id}/discount", new ApplySaleLineDiscountRequest(discount.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{ownerLine.Id}/discount")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{ownerLine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{secondOwnerSale.Lines.Single().Id}", new UpdateSaleLineQuantityRequest(2))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherClient.GetAsync("/api/management/sales")).StatusCode);
        using var manager = await Auth("manager@example.com");
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/management/sales/{sale.Id}")).StatusCode);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Validation_active_states_and_lists_follow_contract()
    {
        await SeedContext("cashier@example.com");
        var (product, percentage, _) = await SeedCatalog(10.005m, 14m);
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(int.MaxValue, 1))).StatusCode);
        await WithDb(async db => { (await db.Products.FindAsync(product.Id))!.IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.GetAsync("/api/cashier/sales?page=0&pageSize=101&sortBy=nope")).StatusCode);
        var list = await cashier.GetFromJsonAsync<PagedResponse<SaleResponse>>("/api/cashier/sales?status=Open&sortBy=totalAmount&sortDirection=asc");
        Assert.Contains(list!.Items, x => x.Id == sale.Id);
        Assert.True(percentage.IsActive);
    }

    [Fact]
    public async Task Relational_rowversion_rejects_a_stale_same_sale_mutation()
    {
        await SeedContext("cashier@example.com");
        using var cashier = await Auth("cashier@example.com");
        var created = await CreateSale(cashier);
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = await firstDb.Sales.SingleAsync(x => x.Id == created.Id);
        var stale = await secondDb.Sales.SingleAsync(x => x.Id == created.Id);
        var originalVersion = first.RowVersion.ToArray();
        first.UpdatedAtUtc = first.UpdatedAtUtc.AddSeconds(1);
        stale.UpdatedAtUtc = stale.UpdatedAtUtc.AddSeconds(2);
        await firstDb.SaveChangesAsync();
        Assert.NotEqual(originalVersion, first.RowVersion);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
        var persisted = await firstDb.Sales.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(first.UpdatedAtUtc, persisted.UpdatedAtUtc);
    }

    [Fact]
    public void Money_rounding_uses_two_decimals_away_from_zero()
    {
        Assert.Equal(1.01m, SaleCalculation.Money(1.005m));
        Assert.Equal(-1.01m, SaleCalculation.Money(-1.005m));
    }

    [Fact]
    public async Task Create_rechecks_persisted_cashier_state_and_endpoint_roles()
    {
        var inactiveAfterLogin = await CreateCashier();
        using var inactiveClient = await Auth(inactiveAfterLogin.Email);
        await WithDb(async db => { (await db.Users.FindAsync(inactiveAfterLogin.Id))!.IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Forbidden, (await inactiveClient.PostAsync("/api/cashier/sales", null)).StatusCode);

        var changedRole = await CreateCashier();
        using var changedRoleClient = await Auth(changedRole.Email);
        await WithDb(async db => { (await db.Users.FindAsync(changedRole.Id))!.Role = UserRole.Manager; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Forbidden, (await changedRoleClient.PostAsync("/api/cashier/sales", null)).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/cashier/sales", null)).StatusCode);
        using var admin = await Auth("admin@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/cashier/sales", null)).StatusCode);
        using var manager = await Auth("manager@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync("/api/cashier/sales", null)).StatusCode);
    }

    [Fact]
    public async Task Discount_mutations_reject_product_discount_identity_collisions_atomically()
    {
        await SeedContext("cashier@example.com");
        var (product, discountA, _) = await SeedCatalog(10m, 14m);
        Discount discountB = null!;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            discountB = new Discount { Name = "Other", Type = DiscountType.Percentage, Value = 5, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(discountB); await db.SaveChangesAsync();
        });
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 1));
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 2, discountA.Id));
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 3, discountB.Id));
        var unchanged = sale;
        var noDiscount = sale.Lines.Single(x => x.DiscountId == null);
        var a = sale.Lines.Single(x => x.DiscountId == discountA.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{noDiscount.Id}/discount", new ApplySaleLineDiscountRequest(discountA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{a.Id}/discount")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{a.Id}/discount", new ApplySaleLineDiscountRequest(discountB.Id))).StatusCode);

        var persisted = await cashier.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}");
        Assert.Equal(unchanged.Lines.Select(x => (x.Id, x.DiscountId, x.Quantity)), persisted!.Lines.Select(x => (x.Id, x.DiscountId, x.Quantity)));
        Assert.Equal(unchanged.TotalAmount, persisted.TotalAmount);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Quantity_and_money_capacity_failures_return_400_and_leave_sale_unchanged()
    {
        await SeedContext("cashier@example.com");
        var (free, _, _) = await SeedCatalog(0m, 0m);
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(free.Id, int.MaxValue));
        var beforeOverflow = sale;
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(free.Id, 1))).StatusCode);
        sale = (await cashier.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}"))!;
        Assert.Equal(int.MaxValue, sale.Lines.Single().Quantity);
        Assert.Equal(beforeOverflow.UpdatedAtUtc, sale.UpdatedAtUtc);

        const decimal maximumCompatible = 9_000_000_000_000_000m;
        var (maximum, _, _) = await SeedCatalog(maximumCompatible, 0m);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(maximum.Id, 1));
        Assert.Equal(maximumCompatible, sale.Lines.Single(x => x.ProductId == maximum.Id).LineTotal);
        var stable = sale;
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{sale.Lines.Single(x => x.ProductId == maximum.Id).Id}", new UpdateSaleLineQuantityRequest(2))).StatusCode);
        Assert.Equal(stable.TotalAmount, (await cashier.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}"))!.TotalAmount);

        var (taxOverflow, _, _) = await SeedCatalog(SaleCalculation.MaximumMoney, 1m);
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(taxOverflow.Id, 1))).StatusCode);
        Assert.DoesNotContain((await cashier.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}"))!.Lines, x => x.ProductId == taxOverflow.Id);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Multiple_individually_valid_lines_cannot_overflow_sale_totals()
    {
        await SeedContext("cashier@example.com");
        var (first, _, _) = await SeedCatalog(6_000_000_000_000_000m, 0m);
        var (second, _, _) = await SeedCatalog(5_000_000_000_000_000m, 0m);
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(first.Id, 1));
        var before = sale;
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(second.Id, 1))).StatusCode);
        sale = (await cashier.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}"))!;
        Assert.Single(sale.Lines);
        Assert.Equal(before.TotalAmount, sale.TotalAmount);
        Assert.Equal(before.UpdatedAtUtc, sale.UpdatedAtUtc);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Real_http_concurrent_quantity_updates_return_one_success_and_one_conflict()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            await SeedContext("cashier@example.com");
            var (product, _, _) = await SeedCatalog(10m, 14m);
            using var firstClient = await Auth("cashier@example.com");
            using var secondClient = await Auth("cashier@example.com");
            var sale = await CreateSale(firstClient);
            sale = await Post<AddSaleLineRequest>(firstClient, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 1));
            var lineId = sale.Lines.Single().Id;
            var coordinator = factory.Services.GetRequiredService<SaleMutationSaveCoordinator>();
            coordinator.Enable();
            try
            {
                var responses = await Task.WhenAll(
                    firstClient.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{lineId}", new UpdateSaleLineQuantityRequest(2)),
                    secondClient.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{lineId}", new UpdateSaleLineQuantityRequest(3)));
                Assert.Equal(2, coordinator.Arrivals);
                Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order().ToArray());
                var persisted = (await firstClient.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}"))!;
                Assert.Contains(persisted.Lines.Single().Quantity, new[] { 2, 3 });
                await AssertTotalsInvariant(sale.Id);
            }
            finally { coordinator.Disable(); }
        }
    }

    [Fact]
    public async Task Http_calculation_path_matches_examples_and_rounds_midpoints_away_from_zero()
    {
        await SeedContext("cashier@example.com");
        var (ten, _, fixedLarge) = await SeedCatalog(10m, 14m);
        var (roundingProduct, roundingDiscount, _) = await SeedCatalog(0.05m, 12.5m);
        await WithDb(async db => { (await db.Discounts.FindAsync(fixedLarge.Id))!.Value = 25m; await db.SaveChangesAsync(); });
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(ten.Id, 2));
        var plain = sale.Lines.Single(x => x.ProductId == ten.Id);
        Assert.Equal((0m, 10m, 1.40m, 11.40m), (plain.UnitDiscountAmount, plain.UnitNetAmount, plain.UnitTaxAmount, plain.UnitTotal));
        Assert.Equal((20m, 0m, 2.80m, 22.80m), (plain.LineSubtotal, plain.LineDiscountTotal, plain.LineTaxTotal, plain.LineTotal));

        sale = await Put(cashier, $"/api/cashier/sales/{sale.Id}/lines/{plain.Id}/discount", new ApplySaleLineDiscountRequest(fixedLarge.Id));
        plain = sale.Lines.Single(x => x.Id == plain.Id);
        Assert.Equal((10m, 0m, 0m, 0m), (plain.UnitDiscountAmount, plain.UnitNetAmount, plain.UnitTaxAmount, plain.UnitTotal));

        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(roundingProduct.Id, 1, roundingDiscount.Id));
        var rounded = sale.Lines.Single(x => x.ProductId == roundingProduct.Id);
        Assert.Equal(0.01m, rounded.UnitDiscountAmount);
        Assert.Equal(0.04m, rounded.UnitNetAmount);
        Assert.Equal(0.01m, rounded.UnitTaxAmount);
        Assert.Equal(0.05m, rounded.UnitTotal);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task One_open_shift_allows_multiple_open_sales_with_fully_server_derived_state()
    {
        var (user, register, shift) = await SeedContext("cashier@example.com");
        using var cashier = await Auth("cashier@example.com");
        var firstResponse = await cashier.PostAsJsonAsync("/api/cashier/sales", new { branchId = int.MaxValue, totalAmount = 99, status = "Completed" });
        var secondResponse = await cashier.PostAsync("/api/cashier/sales", null);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<SaleResponse>())!;
        var second = (await secondResponse.Content.ReadFromJsonAsync<SaleResponse>())!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(shift.Id, first.CashierShiftId);
        Assert.Equal(shift.Id, second.CashierShiftId);
        await WithDb(async db =>
        {
            foreach (var id in new[] { first.Id, second.Id })
            {
                var sale = await db.Sales.AsNoTracking().SingleAsync(x => x.Id == id);
                Assert.Equal((register.BranchId, register.Id, shift.Id, user.Id), (sale.BranchId, sale.RegisterId, sale.CashierShiftId, sale.CashierUserId));
                Assert.Equal(SaleStatus.Open, sale.Status);
                Assert.Null(sale.ReceiptNumber); Assert.Null(sale.CompletedAtUtc); Assert.Null(sale.VoidedAtUtc); Assert.Null(sale.VoidedByUserId); Assert.Null(sale.VoidReason);
                Assert.Equal((0m, 0m, 0m, 0m), (sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.TotalAmount));
                Assert.Equal(sale.CreatedAtUtc, sale.UpdatedAtUtc);
            }
        });
    }

    [Fact]
    public async Task Configuration_active_rules_apply_only_when_selecting_new_configuration()
    {
        await SeedContext("cashier@example.com");
        var (product, discount, inactiveReplacement) = await SeedCatalog(10m, 14m);
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(int.MaxValue, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1, int.MaxValue))).StatusCode);
        await WithDb(async db => { (await db.Products.FindAsync(product.Id))!.IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1))).StatusCode);
        await WithDb(async db => { (await db.Products.FindAsync(product.Id))!.IsActive = true; (await db.TaxRates.FindAsync(product.TaxRateId))!.IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1))).StatusCode);
        await WithDb(async db => { (await db.TaxRates.FindAsync(product.TaxRateId))!.IsActive = true; (await db.Discounts.FindAsync(discount.Id))!.IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1, discount.Id))).StatusCode);

        await WithDb(async db => { (await db.Discounts.FindAsync(discount.Id))!.IsActive = true; (await db.Discounts.FindAsync(inactiveReplacement.Id))!.IsActive = false; await db.SaveChangesAsync(); });
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 1, discount.Id));
        var line = sale.Lines.Single();
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount", new ApplySaleLineDiscountRequest(int.MaxValue))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount", new ApplySaleLineDiscountRequest(inactiveReplacement.Id))).StatusCode);
        await WithDb(async db =>
        {
            (await db.Products.FindAsync(product.Id))!.IsActive = false;
            (await db.TaxRates.FindAsync(product.TaxRateId))!.IsActive = false;
            (await db.Discounts.FindAsync(discount.Id))!.IsActive = false;
            await db.SaveChangesAsync();
        });
        sale = await Put(cashier, $"/api/cashier/sales/{sale.Id}/lines/{line.Id}", new UpdateSaleLineQuantityRequest(2));
        Assert.Equal(2, sale.Lines.Single().Quantity);
        sale = await Delete(cashier, $"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount");
        Assert.Null(sale.Lines.Single().DiscountId);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Closed_shift_non_open_sale_and_persisted_user_state_block_mutations_without_changes()
    {
        var (user, _, shift) = await SeedContext("cashier@example.com");
        var (product, discount, _) = await SeedCatalog(10m, 14m);
        using var cashier = await Auth("cashier@example.com");
        var sale = await CreateSale(cashier);
        sale = await Post<AddSaleLineRequest>(cashier, $"/api/cashier/sales/{sale.Id}/lines", new(product.Id, 1, discount.Id));
        var line = sale.Lines.Single();
        await WithDb(async db => { var s = await db.CashierShifts.FindAsync(shift.Id); s!.Status = CashierShiftStatus.Closed; s.ClosedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); });
        foreach (var response in new[]
        {
            await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1)),
            await cashier.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}", new UpdateSaleLineQuantityRequest(2)),
            await cashier.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount", new ApplySaleLineDiscountRequest(discount.Id)),
            await cashier.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount"),
            await cashier.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}")
        }) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertTotalsInvariant(sale.Id);

        await SeedContext("cashier@example.com");
        var oldShiftResponse = await cashier.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1));
        Assert.Equal(HttpStatusCode.Conflict, oldShiftResponse.StatusCode);
        Assert.Equal("The sale does not belong to the current open cashier shift.", (await oldShiftResponse.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);

        await WithDb(async db => { (await db.Sales.FindAsync(sale.Id))!.Status = SaleStatus.Completed; (await db.Users.FindAsync(user.Id))!.IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}", new UpdateSaleLineQuantityRequest(2))).StatusCode);
        await WithDb(async db => { var u = await db.Users.FindAsync(user.Id); u!.IsActive = true; u.Role = UserRole.Manager; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}")).StatusCode);
        await WithDb(async db => { var u = await db.Users.FindAsync(user.Id); u!.Role = UserRole.Cashier; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}")).StatusCode);
        await AssertTotalsInvariant(sale.Id);
    }

    [Fact]
    public async Task Cashier_list_filters_paginates_and_sorts_only_owned_sales()
    {
        var listCashier = await CreateCashier();
        var (_, _, firstShift) = await SeedContext(listCashier.Email);
        using var cashier = await Auth(listCashier.Email);
        var first = await CreateSale(cashier); var second = await CreateSale(cashier);
        var (_, _, secondShift) = await SeedContext(listCashier.Email);
        var third = await CreateSale(cashier); var fourth = await CreateSale(cashier);
        await WithDb(async db =>
        {
            var values = new[] { (first.Id, 10m, 1), (second.Id, 10m, 1), (third.Id, 30m, 3), (fourth.Id, 20m, 2) };
            foreach (var (id, total, day) in values)
            {
                var sale = await db.Sales.FindAsync(id); sale!.TotalAmount = total; sale.Subtotal = total;
                sale.CreatedAtUtc = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero);
            }
            (await db.Sales.FindAsync(second.Id))!.Status = SaleStatus.Completed;
            await db.SaveChangesAsync();
        });
        var page1 = await cashier.GetFromJsonAsync<PagedResponse<SaleResponse>>("/api/cashier/sales?page=1&pageSize=2&sortBy=createdAt&sortDirection=asc");
        var page2 = await cashier.GetFromJsonAsync<PagedResponse<SaleResponse>>("/api/cashier/sales?page=2&pageSize=2&sortBy=createdAt&sortDirection=asc");
        Assert.Equal(4, page1!.TotalCount); Assert.Equal(2, page1.TotalPages);
        Assert.Equal(new[] { first.Id, second.Id, fourth.Id, third.Id }, page1.Items.Concat(page2!.Items).Select(x => x.Id));
        Assert.Equal(4, page1.Items.Concat(page2.Items).Select(x => x.Id).Distinct().Count());
        var completed = await cashier.GetFromJsonAsync<PagedResponse<SaleResponse>>("/api/cashier/sales?status=Completed");
        Assert.Equal(new[] { second.Id }, completed!.Items.Select(x => x.Id));
        var shiftFilter = await cashier.GetFromJsonAsync<PagedResponse<SaleResponse>>($"/api/cashier/sales?cashierShiftId={firstShift.Id}");
        Assert.Equal(new[] { first.Id, second.Id }.Order(), shiftFilter!.Items.Select(x => x.Id).Order());
        foreach (var (field, direction, expected) in new[]
        {
            ("createdAt", "desc", new[] { third.Id, fourth.Id, first.Id, second.Id }),
            ("totalAmount", "asc", new[] { first.Id, second.Id, fourth.Id, third.Id }),
            ("totalAmount", "desc", new[] { third.Id, fourth.Id, first.Id, second.Id })
        })
        {
            var sorted = await cashier.GetFromJsonAsync<PagedResponse<SaleResponse>>($"/api/cashier/sales?pageSize=100&sortBy={field}&sortDirection={direction}");
            Assert.Equal(expected, sorted!.Items.Select(x => x.Id));
        }
        foreach (var query in new[] { "page=0", "pageSize=0", "pageSize=101", "sortBy=id", "sortDirection=sideways", "sortBy=", "sortDirection=" })
            Assert.Equal(HttpStatusCode.BadRequest, (await cashier.GetAsync($"/api/cashier/sales?{query}")).StatusCode);
        Assert.NotEqual(firstShift.Id, secondShift.Id);
    }

    [Fact]
    public async Task Management_list_authorizes_filters_paginates_sorts_and_details()
    {
        var managedCashier = await CreateCashier();
        var (_, register, shift) = await SeedContext(managedCashier.Email);
        using var owner = await Auth(managedCashier.Email);
        var sales = new[] { await CreateSale(owner), await CreateSale(owner), await CreateSale(owner), await CreateSale(owner) };
        await WithDb(async db =>
        {
            for (var index = 0; index < sales.Length; index++)
            {
                var sale = await db.Sales.FindAsync(sales[index].Id);
                sale!.Subtotal = sale.TotalAmount = new[] { 10m, 10m, 30m, 20m }[index];
                sale.CreatedAtUtc = new DateTimeOffset(2026, 2, new[] { 1, 1, 3, 2 }[index], 0, 0, 0, TimeSpan.Zero);
            }
            (await db.Sales.FindAsync(sales[1].Id))!.Status = SaleStatus.Completed;
            await db.SaveChangesAsync();
        });
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/management/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/management/sales")).StatusCode);
        using var admin = await Auth("admin@example.com"); using var manager = await Auth("manager@example.com");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/management/sales?branchId={register.BranchId}")).StatusCode);
        foreach (var filter in new[]
        {
            $"branchId={register.BranchId}", $"registerId={register.Id}", $"cashierUserId={managedCashier.Id}", $"cashierShiftId={shift.Id}"
        })
        {
            var result = await manager.GetFromJsonAsync<PagedResponse<SaleResponse>>($"/api/management/sales?{filter}&pageSize=100");
            Assert.Equal(sales.Select(x => x.Id).Order(), result!.Items.Select(x => x.Id).Order());
        }
        var completed = await manager.GetFromJsonAsync<PagedResponse<SaleResponse>>($"/api/management/sales?branchId={register.BranchId}&status=Completed");
        Assert.Equal(sales[1].Id, Assert.Single(completed!.Items).Id);
        var firstPage = await manager.GetFromJsonAsync<PagedResponse<SaleResponse>>($"/api/management/sales?branchId={register.BranchId}&page=1&pageSize=2&sortBy=createdAt&sortDirection=asc");
        var secondPage = await manager.GetFromJsonAsync<PagedResponse<SaleResponse>>($"/api/management/sales?branchId={register.BranchId}&page=2&pageSize=2&sortBy=createdAt&sortDirection=asc");
        Assert.Equal(4, firstPage!.TotalCount); Assert.Equal(2, firstPage.TotalPages);
        Assert.Equal(new[] { sales[0].Id, sales[1].Id, sales[3].Id, sales[2].Id }, firstPage.Items.Concat(secondPage!.Items).Select(x => x.Id));
        foreach (var (field, direction, expected) in new[]
        {
            ("createdAt", "desc", new[] { sales[2].Id, sales[3].Id, sales[0].Id, sales[1].Id }),
            ("totalAmount", "asc", new[] { sales[0].Id, sales[1].Id, sales[3].Id, sales[2].Id }),
            ("totalAmount", "desc", new[] { sales[2].Id, sales[3].Id, sales[0].Id, sales[1].Id })
        })
        {
            var sorted = await manager.GetFromJsonAsync<PagedResponse<SaleResponse>>($"/api/management/sales?branchId={register.BranchId}&pageSize=100&sortBy={field}&sortDirection={direction}");
            Assert.Equal(expected, sorted!.Items.Select(x => x.Id));
        }
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/management/sales/{sales[0].Id}")).StatusCode);
        var missing = await manager.GetAsync("/api/management/sales/2147483647");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
    }

    async Task<(User User, Register Register, CashierShift Shift)> SeedContext(string email)
    {
        User user = null!; Register register = null!; CashierShift shift = null!;
        await WithDb(async db =>
        {
            user = await db.Users.SingleAsync(x => x.Email == email);
            var now = DateTimeOffset.UtcNow;
            foreach (var open in await db.CashierShifts.Where(x => x.CashierUserId == user.Id && x.Status == CashierShiftStatus.Open).ToListAsync())
            {
                open.Status = CashierShiftStatus.Closed; open.ClosedAtUtc = now; open.UpdatedAtUtc = now;
            }
            await db.SaveChangesAsync();
            var branch = new Branch { Name = "Branch", Code = Guid.NewGuid().ToString("N"), Address = "Address", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            register = new Register { Branch = branch, Name = "Register", Code = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            shift = new CashierShift { Branch = branch, Register = register, CashierUserId = user.Id, Status = CashierShiftStatus.Open, OpeningFloat = 0, OpenedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(shift); await db.SaveChangesAsync();
        });
        return (user, register, shift);
    }

    async Task<(Product Product, Discount Percentage, Discount Fixed)> SeedCatalog(decimal price, decimal taxPercent)
    {
        Product product = null!; Discount percentage = null!; Discount fixedAmount = null!;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var tax = new TaxRate { Name = "VAT", Percentage = taxPercent, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            product = new Product { Sku = Guid.NewGuid().ToString("N"), Name = "Product", UnitPrice = SaleCalculation.Money(price), TaxRate = tax, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            percentage = new Discount { Name = "Ten percent", Type = DiscountType.Percentage, Value = 10, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            fixedAmount = new Discount { Name = "Large fixed", Type = DiscountType.FixedAmount, Value = 250, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.AddRange(product, percentage, fixedAmount); await db.SaveChangesAsync();
        });
        return (product, percentage, fixedAmount);
    }

    async Task<User> CreateCashier()
    {
        User user = null!;
        await WithDb(async db =>
        {
            using var scope = factory.Services.CreateScope(); var now = DateTimeOffset.UtcNow; var email = $"cashier-{Guid.NewGuid():N}@example.com";
            user = new User { FirstName = "Other", LastName = "Cashier", Email = email, NormalizedEmail = EmailNormalizer.Normalize(email), PasswordHash = "", Role = UserRole.Cashier, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, "Valid1!Password");
            db.Add(user); await db.SaveChangesAsync();
        });
        return user;
    }

    async Task CloseOpenShifts(string email) => await WithDb(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var userId = await db.Users.Where(x => x.Email == email).Select(x => x.Id).SingleAsync();
        foreach (var shift in await db.CashierShifts.Where(x => x.CashierUserId == userId && x.Status == CashierShiftStatus.Open).ToListAsync())
        {
            shift.Status = CashierShiftStatus.Closed; shift.ClosedAtUtc = now; shift.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync();
    });

    async Task<SaleResponse> CreateSale(HttpClient client) => await Read(await client.PostAsync("/api/cashier/sales", null));
    static async Task<SaleResponse> Post<T>(HttpClient client, string url, T body) => await Read(await client.PostAsJsonAsync(url, body));
    static async Task<SaleResponse> Put<T>(HttpClient client, string url, T body) => await Read(await client.PutAsJsonAsync(url, body));
    static async Task<SaleResponse> Delete(HttpClient client, string url) => await Read(await client.DeleteAsync(url));
    static async Task<SaleResponse> Read(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<SaleResponse>())!; }
    async Task<HttpClient> Auth(string email)
    {
        var client = factory.CreateClient(); var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Valid1!Password")); login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken); return client;
    }
    async Task WithDb(Func<AppDbContext, Task> action) { using var scope = factory.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<AppDbContext>()); }

    async Task AssertTotalsInvariant(int saleId) => await WithDb(async db =>
    {
        var sale = await db.Sales.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == saleId);
        Assert.Equal(sale.Lines.Sum(x => x.LineSubtotal), sale.Subtotal);
        Assert.Equal(sale.Lines.Sum(x => x.LineDiscountTotal), sale.DiscountTotal);
        Assert.Equal(sale.Lines.Sum(x => x.LineTaxTotal), sale.TaxTotal);
        Assert.Equal(sale.Lines.Sum(x => x.LineTotal), sale.TotalAmount);
    });
}
