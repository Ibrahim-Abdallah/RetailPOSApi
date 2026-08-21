using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Reports;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed partial class ReportingTests(RetailApiFactory factory) : IClassFixture<RetailApiFactory>
{
    private const string SalesUrl = "/api/management/reports/sales-summary";
    private const string ShiftsUrl = "/api/management/reports/shift-summary";

    [Theory]
    [InlineData(SalesUrl, null, HttpStatusCode.Unauthorized)]
    [InlineData(SalesUrl, "cashier@example.com", HttpStatusCode.Forbidden)]
    [InlineData(SalesUrl, "admin@example.com", HttpStatusCode.OK)]
    [InlineData(SalesUrl, "manager@example.com", HttpStatusCode.OK)]
    [InlineData(ShiftsUrl, null, HttpStatusCode.Unauthorized)]
    [InlineData(ShiftsUrl, "cashier@example.com", HttpStatusCode.Forbidden)]
    [InlineData(ShiftsUrl, "admin@example.com", HttpStatusCode.OK)]
    [InlineData(ShiftsUrl, "manager@example.com", HttpStatusCode.OK)]
    public async Task Reports_enforce_management_authorization(string url, string? email, HttpStatusCode expected)
    {
        using var client = email is null ? factory.CreateClient() : await Auth(email);
        Assert.Equal(expected, (await client.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData(SalesUrl, "fromDate=2026-01-01T00:00:00Z&toDate=2026-01-01T00:00:00Z")]
    [InlineData(SalesUrl, "fromDate=2026-01-02T00:00:00Z&toDate=2026-01-01T00:00:00Z")]
    [InlineData(SalesUrl, "branchId=0")]
    [InlineData(SalesUrl, "registerId=-1")]
    [InlineData(SalesUrl, "cashierUserId=0")]
    [InlineData(ShiftsUrl, "fromDate=2026-01-01T00:00:00Z&toDate=2026-01-01T00:00:00Z")]
    [InlineData(ShiftsUrl, "fromDate=2026-01-02T00:00:00Z&toDate=2026-01-01T00:00:00Z")]
    [InlineData(ShiftsUrl, "branchId=-1")]
    [InlineData(ShiftsUrl, "registerId=0")]
    [InlineData(ShiftsUrl, "cashierUserId=-1")]
    public async Task Reports_reject_invalid_filters(string url, string query)
    {
        using var client = await Auth("admin@example.com");
        var response = await client.GetAsync($"{url}?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(SalesUrl, "fromDate=2099-01-01T00:00:00Z")]
    [InlineData(SalesUrl, "toDate=2000-01-01T00:00:00Z")]
    [InlineData(SalesUrl, "branchId=2147483647")]
    [InlineData(SalesUrl, "registerId=2147483647")]
    [InlineData(SalesUrl, "cashierUserId=2147483647")]
    [InlineData(ShiftsUrl, "fromDate=2099-01-01T00:00:00Z")]
    [InlineData(ShiftsUrl, "toDate=2000-01-01T00:00:00Z")]
    [InlineData(ShiftsUrl, "branchId=2147483647")]
    [InlineData(ShiftsUrl, "registerId=2147483647")]
    [InlineData(ShiftsUrl, "cashierUserId=2147483647")]
    public async Task Valid_single_sided_and_unknown_filters_return_zero_reports(string url, string query)
    {
        using var client = await Auth("manager@example.com");
        var response = await client.GetAsync($"{url}?{query}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(":null", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_sales_report_returns_all_zeros_and_empty_products()
    {
        using var client = await Auth("admin@example.com");
        var value = await client.GetFromJsonAsync<SalesSummaryResponse>($"{SalesUrl}?branchId=2147483647");
        Assert.NotNull(value);
        Assert.Equal(0, value.CompletedSalesCount);
        Assert.Equal(0m, value.GrossSales); Assert.Equal(0m, value.DiscountTotal); Assert.Equal(0m, value.TaxTotal);
        Assert.Equal(0m, value.SalesTotal); Assert.Equal(0m, value.VoidTotal); Assert.Equal(0m, value.RefundTotal);
        Assert.Equal(0m, value.NetSales); Assert.Equal(0m, value.CashPayments); Assert.Equal(0m, value.CardPayments);
        Assert.Equal(0m, value.OtherPayments); Assert.Empty(value.TopProducts);
    }

    [Fact]
    public async Task Sales_summary_uses_historical_snapshots_lifecycle_events_and_amount_applied()
    {
        var data = await SeedContext();
        var at = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        await WithDb(async db =>
        {
            await AddSale(db, data, SaleStatus.PartiallyRefunded, at, 100m, 10m, 9m, 99m,
                (PaymentMethod.Cash, 60m, 100m, PaymentStatus.Completed),
                (PaymentMethod.Card, 39m, 39m, PaymentStatus.Completed),
                (PaymentMethod.Other, 500m, 500m, PaymentStatus.Failed));
            var refunded = await AddSale(db, data, SaleStatus.Refunded, at, 50m, 0m, 5m, 55m,
                (PaymentMethod.Other, 55m, 55m, PaymentStatus.Completed));
            var voided = await AddSale(db, data, SaleStatus.Voided, at, 20m, 2m, 1.8m, 19.8m,
                (PaymentMethod.Cash, 19.8m, 25m, PaymentStatus.Completed));
            voided.VoidedAtUtc = at.AddHours(2);
            db.Refunds.AddRange(
                Refund(refunded, data.User.Id, RefundStatus.Completed, 20m, at.AddHours(1)),
                Refund(refunded, data.User.Id, RefundStatus.Completed, 35m, at.AddHours(2)),
                Refund(refunded, data.User.Id, RefundStatus.Failed, 999m, at.AddHours(2)));
            await db.SaveChangesAsync();
            data.Product.Name = "CURRENT NAME"; data.Product.UnitPrice = 9999m; await db.SaveChangesAsync();
        });

        using var client = await Auth("admin@example.com");
        var url = $"{SalesUrl}?branchId={data.Branch.Id}&fromDate={Uri.EscapeDataString(at.ToString("O"))}&toDate={Uri.EscapeDataString(at.AddDays(1).ToString("O"))}";
        var result = (await client.GetFromJsonAsync<SalesSummaryResponse>(url))!;
        Assert.Equal(3, result.CompletedSalesCount);
        Assert.Equal(170m, result.GrossSales); Assert.Equal(12m, result.DiscountTotal); Assert.Equal(15.8m, result.TaxTotal);
        Assert.Equal(173.8m, result.SalesTotal); Assert.Equal(19.8m, result.VoidTotal); Assert.Equal(55m, result.RefundTotal);
        Assert.Equal(99m, result.NetSales); Assert.Equal(79.8m, result.CashPayments); Assert.Equal(39m, result.CardPayments); Assert.Equal(55m, result.OtherPayments);
        Assert.Equal(result.GrossSales - result.DiscountTotal + result.TaxTotal, result.SalesTotal);
        Assert.Single(result.TopProducts); Assert.Equal("SNAP-SKU", result.TopProducts[0].ProductSku);
        Assert.Equal("Snapshot Product", result.TopProducts[0].ProductName); Assert.Equal(6, result.TopProducts[0].QuantitySold);
        Assert.Equal(173.8m, result.TopProducts[0].SalesTotal);
    }

    [Fact]
    public async Task Activity_timestamps_obey_inclusive_from_and_exclusive_to_boundaries()
    {
        var data = await SeedContext();
        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        await WithDb(async db =>
        {
            await AddSale(db, data, SaleStatus.Completed, start, 10, 0, 0, 10);
            await AddSale(db, data, SaleStatus.Completed, start.AddDays(1), 100, 0, 0, 100);
        });
        using var client = await Auth("manager@example.com");
        var result = (await client.GetFromJsonAsync<SalesSummaryResponse>(Period(SalesUrl, data.Branch.Id, start, start.AddDays(1))))!;
        Assert.Equal(1, result.CompletedSalesCount); Assert.Equal(10m, result.SalesTotal);
    }

    [Fact]
    public async Task Later_refund_and_void_period_can_have_negative_net_sales()
    {
        var data = await SeedContext();
        var period = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        await WithDb(async db =>
        {
            var oldRefunded = await AddSale(db, data, SaleStatus.Refunded, period.AddDays(-2), 30, 0, 0, 30);
            var oldVoided = await AddSale(db, data, SaleStatus.Voided, period.AddDays(-3), 20, 0, 0, 20);
            oldVoided.VoidedAtUtc = period;
            db.Refunds.Add(Refund(oldRefunded, data.User.Id, RefundStatus.Completed, 30, period));
            await db.SaveChangesAsync();
        });
        using var client = await Auth("admin@example.com");
        var result = (await client.GetFromJsonAsync<SalesSummaryResponse>(Period(SalesUrl, data.Branch.Id, period, period.AddDays(1))))!;
        Assert.Equal(0, result.CompletedSalesCount); Assert.Equal(0m, result.SalesTotal);
        Assert.Equal(20m, result.VoidTotal); Assert.Equal(30m, result.RefundTotal); Assert.Equal(-50m, result.NetSales);
    }

    [Fact]
    public async Task Shift_summary_aggregates_closed_snapshots_filters_and_signed_variance()
    {
        var data = await SeedContext();
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await WithDb(async db =>
        {
            db.CashierShifts.Remove(data.Shift); await db.SaveChangesAsync();
            db.CashierShifts.AddRange(
                Shift(data, CashierShiftStatus.Closed, 100, 110, 105, 5, start),
                Shift(data, CashierShiftStatus.Closed, 50, 40, 47.5m, -7.5m, start.AddHours(1)),
                Shift(data, CashierShiftStatus.Closed, 25, null, null, null, start.AddHours(2)),
                Shift(data, CashierShiftStatus.Open, 999, null, null, null, null));
            await db.SaveChangesAsync();
        });
        using var client = await Auth("manager@example.com");
        var result = (await client.GetFromJsonAsync<ShiftSummaryResponse>(Period(ShiftsUrl, data.Branch.Id, start, start.AddDays(1))))!;
        Assert.Equal(3, result.ClosedShiftCount); Assert.Equal(175m, result.OpeningFloatTotal);
        Assert.Equal(150m, result.DeclaredCashTotal); Assert.Equal(152.5m, result.ExpectedCashTotal);
        Assert.Equal(-2.5m, result.CashVarianceTotal); Assert.Equal(5m, result.TotalOverage); Assert.Equal(7.5m, result.TotalShortage);
    }

    private async Task<SeedData> SeedContext()
    {
        SeedData? result = null;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow; var suffix = Guid.NewGuid().ToString("N");
            var user = new User { FirstName = "Report", LastName = "Cashier", Email = $"report-{suffix}@example.com", NormalizedEmail = $"REPORT-{suffix}@EXAMPLE.COM", PasswordHash = "x", Role = UserRole.Cashier, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var branch = new Branch { Name = "Report Branch", Code = $"RB-{suffix}", Address = "A", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.AddRange(user, branch); await db.SaveChangesAsync();
            var register = new Register { BranchId = branch.Id, Name = "Report Register", Code = $"RR-{suffix}", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var tax = new TaxRate { Name = $"Tax-{suffix}", Percentage = 10, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.AddRange(register, tax); await db.SaveChangesAsync();
            var product = new Product { Sku = $"P-{suffix}", Name = "Current Product", UnitPrice = 1, TaxRateId = tax.Id, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var shift = new CashierShift { BranchId = branch.Id, RegisterId = register.Id, CashierUserId = user.Id, Status = CashierShiftStatus.Open, OpeningFloat = 0, OpenedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.AddRange(product, shift); await db.SaveChangesAsync(); result = new(branch, register, user, shift, product, tax);
        });
        return result!;
    }

    private static async Task<Sale> AddSale(AppDbContext db, SeedData data, SaleStatus status, DateTimeOffset completed,
        decimal subtotal, decimal discount, decimal tax, decimal total, params (PaymentMethod Method, decimal Applied, decimal Tendered, PaymentStatus Status)[] payments)
    {
        var sale = new Sale { ReceiptNumber = $"R-{Guid.NewGuid():N}", BranchId = data.Branch.Id, RegisterId = data.Register.Id, CashierShiftId = data.Shift.Id, CashierUserId = data.User.Id, Status = status, Subtotal = subtotal, DiscountTotal = discount, TaxTotal = tax, TotalAmount = total, CompletedAtUtc = completed, CreatedAtUtc = completed.AddMinutes(-1), UpdatedAtUtc = completed };
        sale.Lines.Add(new SaleLine { ProductId = data.Product.Id, ProductSku = "SNAP-SKU", ProductName = "Snapshot Product", Quantity = 2, UnitPrice = subtotal / 2, TaxRateId = data.Tax.Id, TaxRateName = "Snapshot Tax", TaxRatePercentage = 10, UnitDiscountAmount = discount / 2, UnitNetAmount = (subtotal - discount) / 2, UnitTaxAmount = tax / 2, UnitTotal = total / 2, LineSubtotal = subtotal, LineDiscountTotal = discount, LineTaxTotal = tax, LineTotal = total });
        foreach (var p in payments) sale.Payments.Add(new Payment { Method = p.Method, AmountApplied = p.Applied, TenderedAmount = p.Tendered, ChangeAmount = p.Tendered - p.Applied, Status = p.Status, CreatedAtUtc = completed });
        db.Sales.Add(sale); await db.SaveChangesAsync(); return sale;
    }

    private static Refund Refund(Sale sale, int userId, RefundStatus status, decimal total, DateTimeOffset created) => new() { SaleId = sale.Id, ProcessedByUserId = userId, Status = status, Subtotal = total, DiscountTotal = 0, TaxTotal = 0, TotalAmount = total, Reason = "report fixture", CreatedAtUtc = created, UpdatedAtUtc = created };
    private static CashierShift Shift(SeedData d, CashierShiftStatus status, decimal opening, decimal? declared, decimal? expected, decimal? variance, DateTimeOffset? closed) => new() { BranchId = d.Branch.Id, RegisterId = d.Register.Id, CashierUserId = d.User.Id, Status = status, OpeningFloat = opening, OpenedAtUtc = (closed ?? DateTimeOffset.UtcNow).AddHours(-8), ClosedAtUtc = closed, DeclaredCash = declared, ExpectedCash = expected, CashVariance = variance, CreatedAtUtc = (closed ?? DateTimeOffset.UtcNow).AddHours(-8), UpdatedAtUtc = closed ?? DateTimeOffset.UtcNow };
    private static string Period(string url, int branchId, DateTimeOffset from, DateTimeOffset to) => $"{url}?branchId={branchId}&fromDate={Uri.EscapeDataString(from.ToString("O"))}&toDate={Uri.EscapeDataString(to.ToString("O"))}";
    private async Task WithDb(Func<AppDbContext, Task> action) { using var scope = factory.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<AppDbContext>()); }
    private async Task<HttpClient> Auth(string email) { var client = factory.CreateClient(); var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Valid1!Password")); response.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken); return client; }
    private sealed record SeedData(Branch Branch, Register Register, User User, CashierShift Shift, Product Product, TaxRate Tax);
}
