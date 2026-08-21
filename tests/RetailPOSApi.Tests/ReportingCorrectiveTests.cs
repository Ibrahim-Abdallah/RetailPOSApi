using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Reports;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Tests;

public sealed partial class ReportingTests
{
    [Fact]
    public async Task Sales_branch_filter_constrains_every_activity_metric_and_products() => await AssertSalesDimension("branch");

    [Fact]
    public async Task Sales_register_filter_constrains_every_activity_metric_and_products() => await AssertSalesDimension("register");

    [Fact]
    public async Task Sales_cashier_filter_constrains_every_activity_metric_and_products() => await AssertSalesDimension("cashier");

    [Fact]
    public async Task Sales_combined_filters_return_the_exact_intersection() => await AssertSalesDimension("combined");

    [Fact]
    public async Task Top_products_aggregate_quantity_and_stored_line_total()
    {
        var d = await SeedContext(); var at = Utc(10, 1); await WithDb(async db =>
        {
            await AddSale(db, d, SaleStatus.Completed, at, 12, 0, 0, 12);
            await AddSale(db, d, SaleStatus.Completed, at.AddMinutes(1), 18, 0, 0, 18);
        });
        var r = await Sales(d, at, at.AddDays(1));
        Assert.Single(r.TopProducts); Assert.Equal(4, r.TopProducts[0].QuantitySold); Assert.Equal(30m, r.TopProducts[0].SalesTotal);
    }

    [Fact]
    public async Task Top_products_return_five_and_apply_all_three_ordering_keys()
    {
        var d = await SeedContext(); var at = Utc(10, 2); int[] ids = [];
        await WithDb(async db =>
        {
            var products = Enumerable.Range(1, 6).Select(i => new Product { Sku = $"TOP-{Guid.NewGuid():N}", Name = $"Top {i}", UnitPrice = i, TaxRateId = d.Tax.Id, IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = at }).ToArray();
            db.Products.AddRange(products); await db.SaveChangesAsync(); ids = products.Select(x => x.Id).ToArray();
            await AddSnapshotSale(db, d, products[0], at, 1, 60); // highest total
            await AddSnapshotSale(db, d, products[1], at, 3, 30); // total tie, higher quantity
            await AddSnapshotSale(db, d, products[2], at, 2, 30); // total tie, lower quantity
            await AddSnapshotSale(db, d, products[3], at, 2, 20); // exact tie resolved by id
            await AddSnapshotSale(db, d, products[4], at, 2, 20);
            await AddSnapshotSale(db, d, products[5], at, 1, 1);  // sixth omitted
        });
        var r = await Sales(d, at, at.AddDays(1));
        Assert.Equal(5, r.TopProducts.Count);
        Assert.Equal(new[] { ids[0], ids[1], ids[2], ids[3], ids[4] }, r.TopProducts.Select(x => x.ProductId));
    }

    [Fact]
    public async Task Top_products_remain_historical_after_catalog_tax_and_discount_changes()
    {
        var d = await SeedContext(); var at = Utc(10, 3); await WithDb(async db =>
        {
            var discount = new Discount { Name = "Old Discount", Type = DiscountType.Percentage, Value = 5, IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = at };
            db.Discounts.Add(discount); await db.SaveChangesAsync();
            await AddSnapshotSale(db, d, d.Product, at, 3, 37.77m, "HIST-SKU", "Historical Name", discount);
            d.Product.Name = "Changed"; d.Product.UnitPrice = 999; d.Tax.Percentage = 99; discount.Value = 100; await db.SaveChangesAsync();
        });
        var p = Assert.Single((await Sales(d, at, at.AddDays(1))).TopProducts);
        Assert.Equal("HIST-SKU", p.ProductSku); Assert.Equal("Historical Name", p.ProductName); Assert.Equal(3, p.QuantitySold); Assert.Equal(37.77m, p.SalesTotal);
    }

    [Fact]
    public async Task Every_current_lifecycle_status_retains_original_completion_activity()
    {
        var d = await SeedContext(); var at = Utc(10, 4); await WithDb(async db =>
        {
            foreach (var status in new[] { SaleStatus.Completed, SaleStatus.PartiallyRefunded, SaleStatus.Refunded, SaleStatus.Voided })
                await AddSale(db, d, status, at, 10, 0, 0, 10);
        });
        var r = await Sales(d, at, at.AddDays(1)); Assert.Equal(4, r.CompletedSalesCount); Assert.Equal(40m, r.SalesTotal); Assert.Equal(8, r.TopProducts.Single().QuantitySold);
    }

    [Fact]
    public async Task Repeated_completed_refunds_aggregate_while_pending_and_failed_are_excluded()
    {
        var d = await SeedContext(); var at = Utc(10, 5); await WithDb(async db =>
        {
            var s = await AddSale(db, d, SaleStatus.PartiallyRefunded, at, 100, 0, 0, 100);
            db.Refunds.AddRange(Refund(s, d.User.Id, RefundStatus.Completed, 12.34m, at), Refund(s, d.User.Id, RefundStatus.Completed, 7.66m, at), Refund(s, d.User.Id, RefundStatus.Pending, 50, at), Refund(s, d.User.Id, RefundStatus.Failed, 60, at)); await db.SaveChangesAsync();
        });
        var r = await Sales(d, at, at.AddDays(1)); Assert.Equal(100m, r.SalesTotal); Assert.Equal(20m, r.RefundTotal); Assert.Equal(80m, r.NetSales);
    }

    [Fact]
    public async Task Fully_refunded_sale_keeps_original_total_and_is_subtracted_once()
    {
        var d = await SeedContext(); var at = Utc(10, 6); await WithDb(async db => { var s = await AddSale(db, d, SaleStatus.Refunded, at, 45, 0, 0, 45); db.Refunds.Add(Refund(s, d.User.Id, RefundStatus.Completed, 45, at)); await db.SaveChangesAsync(); });
        var r = await Sales(d, at, at.AddDays(1)); Assert.Equal(1, r.CompletedSalesCount); Assert.Equal(45m, r.SalesTotal); Assert.Equal(45m, r.RefundTotal); Assert.Equal(0m, r.NetSales);
    }

    [Fact]
    public async Task Voided_sale_keeps_original_total_and_void_is_subtracted_once()
    {
        var d = await SeedContext(); var at = Utc(10, 7); await WithDb(async db => { var s = await AddSale(db, d, SaleStatus.Voided, at, 32, 0, 0, 32); s.VoidedAtUtc = at; await db.SaveChangesAsync(); });
        var r = await Sales(d, at, at.AddDays(1)); Assert.Equal(32m, r.SalesTotal); Assert.Equal(32m, r.VoidTotal); Assert.Equal(0m, r.NetSales);
    }

    [Fact]
    public async Task Refund_boundaries_use_created_time_inclusive_from_exclusive_to()
    {
        var d = await SeedContext(); var at = Utc(10, 8); await WithDb(async db => { var s = await AddSale(db, d, SaleStatus.Refunded, at.AddDays(-1), 30, 0, 0, 30); db.Refunds.AddRange(Refund(s, d.User.Id, RefundStatus.Completed, 10, at), Refund(s, d.User.Id, RefundStatus.Completed, 20, at.AddDays(1))); await db.SaveChangesAsync(); });
        var r = await Sales(d, at, at.AddDays(1)); Assert.Equal(10m, r.RefundTotal); Assert.Equal(-10m, r.NetSales);
    }

    [Fact]
    public async Task Void_boundaries_are_inclusive_from_and_exclusive_to()
    {
        var d = await SeedContext(); var at = Utc(10, 9); await WithDb(async db => { var a = await AddSale(db, d, SaleStatus.Voided, at.AddDays(-1), 11, 0, 0, 11); a.VoidedAtUtc = at; var b = await AddSale(db, d, SaleStatus.Voided, at.AddDays(-1), 22, 0, 0, 22); b.VoidedAtUtc = at.AddDays(1); await db.SaveChangesAsync(); });
        Assert.Equal(11m, (await Sales(d, at, at.AddDays(1))).VoidTotal);
    }

    [Fact]
    public async Task Shift_boundaries_are_inclusive_from_and_exclusive_to()
    {
        var d = await SeedContext(); var at = Utc(10, 10); await ReplaceWithClosedShifts(d, Shift(d, CashierShiftStatus.Closed, 10, 10, 10, 0, at), Shift(d, CashierShiftStatus.Closed, 20, 20, 20, 0, at.AddDays(1)));
        var r = await Shifts(d, at, at.AddDays(1)); Assert.Equal(1, r.ClosedShiftCount); Assert.Equal(10m, r.OpeningFloatTotal);
    }

    [Fact]
    public async Task Nonzero_offset_query_is_compared_by_utc_instant_for_all_event_storage_types()
    {
        var d = await SeedContext(); var utc = Utc(10, 11); await WithDb(async db => { var s = await AddSale(db, d, SaleStatus.Voided, utc, 10, 0, 0, 10); s.VoidedAtUtc = utc; db.Refunds.Add(Refund(s, d.User.Id, RefundStatus.Completed, 2, utc)); await db.SaveChangesAsync(); });
        var from = utc.ToOffset(TimeSpan.FromHours(3)); var r = await Sales(d, from, from.AddMinutes(1)); Assert.Equal(1, r.CompletedSalesCount); Assert.Equal(10m, r.VoidTotal); Assert.Equal(2m, r.RefundTotal);
    }

    [Fact]
    public async Task Payment_methods_split_and_same_method_payments_use_amount_applied_not_tendered_or_change()
    {
        var d = await SeedContext(); var at = Utc(10, 12); await WithDb(async db => await AddSale(db, d, SaleStatus.Completed, at, 35, 0, 0, 35, (PaymentMethod.Cash, 5, 20, PaymentStatus.Completed), (PaymentMethod.Cash, 7, 10, PaymentStatus.Completed), (PaymentMethod.Card, 13, 13, PaymentStatus.Completed), (PaymentMethod.Other, 10, 10, PaymentStatus.Completed)));
        var r = await Sales(d, at, at.AddDays(1)); Assert.Equal(12m, r.CashPayments); Assert.Equal(13m, r.CardPayments); Assert.Equal(10m, r.OtherPayments);
    }

    [Fact]
    public async Task Noncompleted_payments_are_excluded_and_zero_total_sale_needs_no_payments()
    {
        var d = await SeedContext(); var at = Utc(10, 13); await WithDb(async db => { await AddSale(db, d, SaleStatus.Completed, at, 0, 0, 0, 0); await AddSale(db, d, SaleStatus.Completed, at, 1, 0, 0, 1, (PaymentMethod.Cash, 99, 99, PaymentStatus.Pending), (PaymentMethod.Card, 88, 88, PaymentStatus.Failed), (PaymentMethod.Other, 77, 77, PaymentStatus.Refunded)); });
        var r = await Sales(d, at, at.AddDays(1)); Assert.Equal(2, r.CompletedSalesCount); Assert.Equal(0m, r.CashPayments); Assert.Equal(0m, r.CardPayments); Assert.Equal(0m, r.OtherPayments);
    }

    [Fact]
    public async Task Shift_branch_filter_returns_exact_persisted_snapshots() => await AssertShiftDimension("branch");
    [Fact]
    public async Task Shift_register_filter_returns_exact_persisted_snapshots() => await AssertShiftDimension("register");
    [Fact]
    public async Task Shift_cashier_filter_returns_exact_persisted_snapshots() => await AssertShiftDimension("cashier");
    [Fact]
    public async Task Shift_combined_filters_handle_zero_and_null_variances() => await AssertShiftDimension("combined");

    [Fact]
    public async Task Representative_single_sided_and_unknown_filters_deserialize_to_exact_zero_reports()
    {
        using var client = await Auth("manager@example.com");
        foreach (var query in new[] { "fromDate=2099-01-01T00:00:00Z", "toDate=2000-01-01T00:00:00Z", "branchId=2147483647", "registerId=2147483647", "cashierUserId=2147483647" })
        {
            AssertSalesZero((await client.GetFromJsonAsync<SalesSummaryResponse>($"{SalesUrl}?{query}"))!);
            AssertShiftZero((await client.GetFromJsonAsync<ShiftSummaryResponse>($"{ShiftsUrl}?{query}"))!);
        }
    }

    private async Task AssertSalesDimension(string dimension)
    {
        var a = await SeedContext(); var b = await SeedContext(); var at = Utc(11, dimension.Length);
        await WithDb(async db =>
        {
            var sa = await AddSale(db, a, SaleStatus.Voided, at, 50, 5, 5, 50, (PaymentMethod.Cash, 10, 20, PaymentStatus.Completed), (PaymentMethod.Card, 20, 20, PaymentStatus.Completed), (PaymentMethod.Other, 20, 20, PaymentStatus.Completed)); sa.VoidedAtUtc = at;
            db.Refunds.Add(Refund(sa, a.User.Id, RefundStatus.Completed, 7, at));
            var sb = await AddSale(db, b, SaleStatus.Voided, at, 500, 0, 0, 500, (PaymentMethod.Cash, 500, 500, PaymentStatus.Completed)); sb.VoidedAtUtc = at; db.Refunds.Add(Refund(sb, b.User.Id, RefundStatus.Completed, 100, at)); await db.SaveChangesAsync();
        });
        var suffix = dimension switch { "branch" => $"branchId={a.Branch.Id}", "register" => $"registerId={a.Register.Id}", "cashier" => $"cashierUserId={a.User.Id}", _ => $"branchId={a.Branch.Id}&registerId={a.Register.Id}&cashierUserId={a.User.Id}" };
        var r = await SalesByQuery(suffix, at, at.AddDays(1)); Assert.Equal(1, r.CompletedSalesCount); Assert.Equal(50m, r.SalesTotal); Assert.Equal(50m, r.VoidTotal); Assert.Equal(7m, r.RefundTotal); Assert.Equal(-7m, r.NetSales); Assert.Equal(10m, r.CashPayments); Assert.Equal(20m, r.CardPayments); Assert.Equal(20m, r.OtherPayments); Assert.Single(r.TopProducts); Assert.Equal(a.Product.Id, r.TopProducts[0].ProductId);
    }

    private async Task AssertShiftDimension(string dimension)
    {
        var a = await SeedContext(); var b = await SeedContext(); var at = Utc(12, dimension.Length);
        await ReplaceWithClosedShifts(a, Shift(a, CashierShiftStatus.Closed, 100, 110, 105, 5, at), Shift(a, CashierShiftStatus.Closed, 50, 40, 47, -7, at.AddMinutes(1)), Shift(a, CashierShiftStatus.Closed, 25, null, null, null, at.AddMinutes(2)), Shift(a, CashierShiftStatus.Closed, 10, 10, 10, 0, at.AddMinutes(3)));
        await ReplaceWithClosedShifts(b, Shift(b, CashierShiftStatus.Closed, 999, 999, 999, 99, at));
        var suffix = dimension switch { "branch" => $"branchId={a.Branch.Id}", "register" => $"registerId={a.Register.Id}", "cashier" => $"cashierUserId={a.User.Id}", _ => $"branchId={a.Branch.Id}&registerId={a.Register.Id}&cashierUserId={a.User.Id}" };
        var r = await ShiftsByQuery(suffix, at, at.AddDays(1)); Assert.Equal(4, r.ClosedShiftCount); Assert.Equal(185m, r.OpeningFloatTotal); Assert.Equal(160m, r.DeclaredCashTotal); Assert.Equal(162m, r.ExpectedCashTotal); Assert.Equal(-2m, r.CashVarianceTotal); Assert.Equal(5m, r.TotalOverage); Assert.Equal(7m, r.TotalShortage);
    }

    private async Task ReplaceWithClosedShifts(SeedData d, params CashierShift[] shifts) => await WithDb(async db => { db.CashierShifts.Remove(d.Shift); await db.SaveChangesAsync(); db.CashierShifts.AddRange(shifts); await db.SaveChangesAsync(); });
    private async Task<SalesSummaryResponse> Sales(SeedData d, DateTimeOffset from, DateTimeOffset to) => await SalesByQuery($"branchId={d.Branch.Id}", from, to);
    private async Task<SalesSummaryResponse> SalesByQuery(string query, DateTimeOffset from, DateTimeOffset to) { using var c = await Auth("admin@example.com"); return (await c.GetFromJsonAsync<SalesSummaryResponse>($"{SalesUrl}?{query}&fromDate={Uri.EscapeDataString(from.ToString("O"))}&toDate={Uri.EscapeDataString(to.ToString("O"))}"))!; }
    private async Task<ShiftSummaryResponse> Shifts(SeedData d, DateTimeOffset from, DateTimeOffset to) => await ShiftsByQuery($"branchId={d.Branch.Id}", from, to);
    private async Task<ShiftSummaryResponse> ShiftsByQuery(string query, DateTimeOffset from, DateTimeOffset to) { using var c = await Auth("manager@example.com"); return (await c.GetFromJsonAsync<ShiftSummaryResponse>($"{ShiftsUrl}?{query}&fromDate={Uri.EscapeDataString(from.ToString("O"))}&toDate={Uri.EscapeDataString(to.ToString("O"))}"))!; }
    private static DateTimeOffset Utc(int month, int day) => new(2026, month, day, 0, 0, 0, TimeSpan.Zero);
    private static void AssertSalesZero(SalesSummaryResponse r) { Assert.Equal(0, r.CompletedSalesCount); Assert.Equal(0m, r.GrossSales); Assert.Equal(0m, r.DiscountTotal); Assert.Equal(0m, r.TaxTotal); Assert.Equal(0m, r.SalesTotal); Assert.Equal(0m, r.VoidTotal); Assert.Equal(0m, r.RefundTotal); Assert.Equal(0m, r.NetSales); Assert.Equal(0m, r.CashPayments); Assert.Equal(0m, r.CardPayments); Assert.Equal(0m, r.OtherPayments); Assert.Empty(r.TopProducts); }
    private static void AssertShiftZero(ShiftSummaryResponse r) { Assert.Equal(0, r.ClosedShiftCount); Assert.Equal(0m, r.OpeningFloatTotal); Assert.Equal(0m, r.DeclaredCashTotal); Assert.Equal(0m, r.ExpectedCashTotal); Assert.Equal(0m, r.CashVarianceTotal); Assert.Equal(0m, r.TotalOverage); Assert.Equal(0m, r.TotalShortage); }

    private static async Task AddSnapshotSale(AppDbContext db, SeedData d, Product product, DateTimeOffset at, int quantity, decimal total, string? sku = null, string? name = null, Discount? discount = null)
    {
        var sale = new Sale { ReceiptNumber = $"R-{Guid.NewGuid():N}", BranchId = d.Branch.Id, RegisterId = d.Register.Id, CashierShiftId = d.Shift.Id, CashierUserId = d.User.Id, Status = SaleStatus.Completed, Subtotal = total, TotalAmount = total, CompletedAtUtc = at, CreatedAtUtc = at, UpdatedAtUtc = at };
        sale.Lines.Add(new SaleLine { ProductId = product.Id, ProductSku = sku ?? product.Sku, ProductName = name ?? product.Name, Quantity = quantity, UnitPrice = total / quantity, DiscountId = discount?.Id, DiscountName = discount?.Name, DiscountType = discount?.Type, DiscountValue = discount?.Value, UnitNetAmount = total / quantity, TaxRateId = d.Tax.Id, TaxRateName = d.Tax.Name, TaxRatePercentage = d.Tax.Percentage, UnitTotal = total / quantity, LineSubtotal = total, LineTotal = total });
        db.Sales.Add(sale); await db.SaveChangesAsync();
    }
}
