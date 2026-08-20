using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Tests;

public sealed class SaleLifecycleTests(RetailApiFactory factory) : IClassFixture<RetailApiFactory>
{
    [Fact]
    public async Task Lifecycle_endpoints_require_management_roles()
    {
        var sale = await SeedSale();
        using var anonymous = factory.CreateClient();
        using var cashier = await Auth("cashier@example.com");
        foreach (var response in new[] {
            await anonymous.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("reason")),
            await anonymous.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds", new ProcessRefundRequest("reason", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m)])) })
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("reason"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds", new ProcessRefundRequest("reason", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/management/sales/{sale.Id}/refunds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/management/sales/{sale.Id}/refunds")).StatusCode);
    }

    [Fact]
    public async Task Missing_sale_returns_not_found_for_every_lifecycle_endpoint()
    {
        using var client = await Auth("admin@example.com"); const int missing = int.MaxValue;
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/management/sales/{missing}/void", new VoidSaleRequest("reason"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/management/sales/{missing}/refunds", new ProcessRefundRequest("reason", [new(1, 1)], []))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/management/sales/{missing}/refunds")).StatusCode);
    }

    [Theory]
    [InlineData("admin@example.com")]
    [InlineData("manager@example.com")]
    public async Task Completed_sale_can_be_voided_with_audited_trimmed_reason(string email)
    {
        var sale = await SeedSale(); using var client = await Auth(email);
        var response = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("  customer request  "));
        response.EnsureSuccessStatusCode(); var value = (await response.Content.ReadFromJsonAsync<SaleResponse>())!;
        Assert.Equal(SaleStatus.Voided, value.Status); Assert.Equal("customer request", value.VoidReason);
        Assert.NotNull(value.VoidedAtUtc); Assert.NotNull(value.VoidedByUserId); Assert.Equal(sale.Receipt, value.ReceiptNumber);
        Assert.Equal(value.VoidedAtUtc, value.UpdatedAtUtc); Assert.Equal(email.StartsWith("admin") ? "Admin User" : "Manager User", value.VoidedByName);
        Assert.Single(value.Lines); Assert.Single(value.Payments);
        Assert.DoesNotContain("rowVersion", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("again"))).StatusCode);
    }

    [Theory]
    [InlineData(SaleStatus.Open)]
    [InlineData(SaleStatus.Voided)]
    [InlineData(SaleStatus.PartiallyRefunded)]
    [InlineData(SaleStatus.Refunded)]
    public async Task Void_rejects_every_ineligible_sale_state(SaleStatus status)
    {
        var sale = await SeedSale(); await SetStatus(sale.Id, status); using var client = await Auth("manager@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("reason"))).StatusCode);
    }

    [Fact]
    public async Task Existing_refund_history_blocks_void_even_when_sale_status_is_completed()
    {
        var sale = await SeedSale(); await SeedCompletedRefund(sale.Id, sale.LineId, 1, sale.PaymentId, 10m); using var client = await Auth("admin@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("reason"))).StatusCode);
    }

    [Fact]
    public async Task Void_validation_rejects_whitespace_and_trimmed_overlength_reason_and_preserves_financial_history()
    {
        var sale = await SeedSale(quantity: 2); using var client = await Auth("admin@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("  " + new string('x', 501) + "  "))).StatusCode);
        var before = await WithDbResult(db => db.Sales.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments).SingleAsync(x => x.Id == sale.Id));
        (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest(" valid "))).EnsureSuccessStatusCode();
        var after = await WithDbResult(db => db.Sales.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments).SingleAsync(x => x.Id == sale.Id));
        Assert.Equal((before.ReceiptNumber, before.CompletedAtUtc, before.Subtotal, before.DiscountTotal, before.TaxTotal, before.TotalAmount),
            (after.ReceiptNumber, after.CompletedAtUtc, after.Subtotal, after.DiscountTotal, after.TaxTotal, after.TotalAmount));
        Assert.Equal(before.Lines.Select(x => (x.Id, x.Quantity, x.LineTotal)), after.Lines.Select(x => (x.Id, x.Quantity, x.LineTotal)));
        Assert.Equal(before.Payments.Select(x => (x.Id, x.AmountApplied, x.TenderedAmount, x.ChangeAmount, x.Status)), after.Payments.Select(x => (x.Id, x.AmountApplied, x.TenderedAmount, x.ChangeAmount, x.Status)));
    }

    [Theory]
    [InlineData(SaleStatus.Open)]
    [InlineData(SaleStatus.Voided)]
    [InlineData(SaleStatus.Refunded)]
    public async Task Refund_rejects_ineligible_sale_states(SaleStatus status)
    {
        var sale = await SeedSale(); await SetStatus(sale.Id, status); using var client = await Auth("admin@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds", RefundRequest(sale))).StatusCode);
    }

    [Fact]
    public async Task Refund_request_shape_validation_rejects_invalid_reason_lines_payments_and_references()
    {
        var sale = await SeedSale(); using var client = await Auth("manager@example.com"); var url = $"/api/management/sales/{sale.Id}/refunds";
        ProcessRefundRequest[] invalid = [
            new(" ", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m)]),
            new("  " + new string('r', 501) + "  ", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m)]),
            new("x", [], [new(sale.PaymentId, 10m)]), new("x", [new(sale.LineId, 0)], [new(sale.PaymentId, 10m)]),
            new("x", [new(sale.LineId, 1), new(sale.LineId, 1)], [new(sale.PaymentId, 10m)]),
            new("x", [new(sale.LineId, 1)], [new(0, 10m)]), new("x", [new(sale.LineId, 1)], [new(sale.PaymentId, 0m)]),
            new("x", [new(sale.LineId, 1)], [new(sale.PaymentId, 10.001m)]),
            new("x", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m, "  " + new string('e', 201) + "  ")]),
            new("x", [new(sale.LineId, 1)], [new(sale.PaymentId, 5m), new(sale.PaymentId, 5m)]) ];
        foreach (var request in invalid) Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request)).StatusCode);
        var nullLines = await client.PostAsJsonAsync(url, new { reason = "x", lines = (object?)null, payments = Array.Empty<object>() });
        var nullPayments = await client.PostAsJsonAsync(url, new { reason = "x", lines = new[] { new { saleLineId = sale.LineId, quantity = 1 } }, payments = (object?)null });
        Assert.Equal(HttpStatusCode.BadRequest, nullLines.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, nullPayments.StatusCode);
    }

    [Fact]
    public async Task Partial_then_full_refund_uses_snapshots_and_payment_capacity_and_returns_history()
    {
        var sale = await SeedSale(quantity: 2, unitPrice: 10m); using var client = await Auth("manager@example.com");
        var first = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest(" first ", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m, " ref ")]));
        first.EnsureSuccessStatusCode(); var one = (await first.Content.ReadFromJsonAsync<RefundResponse>())!;
        Assert.Equal(10m, one.TotalAmount); Assert.Equal(PaymentMethod.Cash, Assert.Single(one.Payments).Method);
        Assert.Equal("ref", one.Payments[0].ExternalReference);

        await WithDb(async db => { var product = await db.Products.SingleAsync(x => x.Id == sale.ProductId); product.UnitPrice = 999m; product.Name = "Changed"; product.IsActive = false; await db.SaveChangesAsync(); });
        var over = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest("over", [new(sale.LineId, 2)], [new(sale.PaymentId, 20m)]));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        var second = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest("second", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m)]));
        second.EnsureSuccessStatusCode(); Assert.Equal(10m, (await second.Content.ReadFromJsonAsync<RefundResponse>())!.TotalAmount);
        var persisted = await WithDbResult(db => db.Sales.AsNoTracking().SingleAsync(x => x.Id == sale.Id));
        Assert.Equal(SaleStatus.Refunded, persisted.Status); Assert.Equal(20m, persisted.TotalAmount);
        var history = await client.GetFromJsonAsync<List<RefundResponse>>($"/api/management/sales/{sale.Id}/refunds");
        Assert.Equal(2, history!.Count); Assert.All(history, x => Assert.Equal("Product", Assert.Single(x.Lines).ProductName));
    }

    [Fact]
    public async Task Multi_line_sequential_refunds_transition_to_refunded_only_after_every_line_is_returned()
    {
        var sale = await SeedTwoLineSale(); using var client = await Auth("manager@example.com");
        var first = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest("line one", [new(sale.FirstLineId, 1)], [new(sale.PaymentId, 10m)]));
        first.EnsureSuccessStatusCode(); Assert.Equal(SaleStatus.PartiallyRefunded, await SaleStatusOf(sale.Id));
        var second = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest("line two", [new(sale.SecondLineId, 1)], [new(sale.PaymentId, 15m)]));
        second.EnsureSuccessStatusCode(); Assert.Equal(SaleStatus.Refunded, await SaleStatusOf(sale.Id));
        var over = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest("again", [new(sale.FirstLineId, 1)], [new(sale.PaymentId, 10m)]));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal(2, await WithDbResult(db => db.RefundLines.CountAsync(x => x.Refund.SaleId == sale.Id)));
    }

    [Fact]
    public async Task Historical_discount_tax_and_product_snapshots_are_authoritative()
    {
        var sale = await SeedHistoricalSale(); using var client = await Auth("admin@example.com");
        await WithDb(async db =>
        {
            var product = await db.Products.SingleAsync(x => x.Id == sale.ProductId); product.Name = "Changed product"; product.UnitPrice = 999m; product.IsActive = false;
            var tax = await db.TaxRates.SingleAsync(x => x.Id == sale.TaxId); tax.Name = "Changed tax"; tax.Percentage = 99m; tax.IsActive = false;
            var discount = await db.Discounts.SingleAsync(x => x.Id == sale.DiscountId); discount.Name = "Changed discount"; discount.Value = 90m; discount.IsActive = false;
            await db.SaveChangesAsync();
        });
        var response = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest(" historical ", [new(sale.LineId, 2)], [new(sale.PaymentId, 19.80m, " historical-ref ")]));
        response.EnsureSuccessStatusCode(); var refund = (await response.Content.ReadFromJsonAsync<RefundResponse>())!; var line = Assert.Single(refund.Lines);
        Assert.Equal((20m, 4m, 3.80m, 19.80m), (line.Subtotal, line.DiscountTotal, line.TaxTotal, line.TotalAmount));
        Assert.Equal((sale.Sku, "Historical product"), (line.ProductSku, line.ProductName)); Assert.Equal("historical", refund.Reason);
    }

    [Fact]
    public async Task Payment_rules_enforce_ownership_status_exact_allocation_and_amount_applied_capacity()
    {
        var sale = await SeedSale(); var foreign = await SeedSale(); using var client = await Auth("manager@example.com"); var url = $"/api/management/sales/{sale.Id}/refunds";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new ProcessRefundRequest("foreign", [new(sale.LineId, 1)], [new(foreign.PaymentId, 10m)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new ProcessRefundRequest("under", [new(sale.LineId, 1)], [new(sale.PaymentId, 9m)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new ProcessRefundRequest("over", [new(sale.LineId, 1)], [new(sale.PaymentId, 11m)]))).StatusCode);
        await WithDb(async db => { var payment = await db.Payments.SingleAsync(x => x.Id == sale.PaymentId); payment.Status = PaymentStatus.Pending; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, RefundRequest(sale))).StatusCode);
        await WithDb(async db => { var payment = await db.Payments.SingleAsync(x => x.Id == sale.PaymentId); payment.Status = PaymentStatus.Completed; await db.SaveChangesAsync(); });
        var exact = await client.PostAsJsonAsync(url, new ProcessRefundRequest(" exact ", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m, " ref ")]));
        exact.EnsureSuccessStatusCode(); var refund = (await exact.Content.ReadFromJsonAsync<RefundResponse>())!;
        Assert.Equal(PaymentMethod.Cash, Assert.Single(refund.Payments).Method); Assert.Equal("ref", refund.Payments[0].ExternalReference);
        var original = await WithDbResult(db => db.Payments.AsNoTracking().SingleAsync(x => x.Id == sale.PaymentId));
        Assert.Equal((10m, 15m, 5m, PaymentStatus.Completed), (original.AmountApplied, original.TenderedAmount, original.ChangeAmount, original.Status));
    }

    [Fact]
    public async Task Cash_capacity_uses_amount_applied_not_tendered_and_prior_allocations_reduce_capacity()
    {
        var cash = await SeedSale(unitPrice: 15m); using var client = await Auth("admin@example.com"); var cashUrl = $"/api/management/sales/{cash.Id}/refunds";
        await WithDb(async db => { var payment = await db.Payments.SingleAsync(x => x.Id == cash.PaymentId); payment.AmountApplied = 10m; payment.TenderedAmount = 20m; payment.ChangeAmount = 10m; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(cashUrl, new ProcessRefundRequest("cash capacity", [new(cash.LineId, 1)], [new(cash.PaymentId, 15m)]))).StatusCode);
        Assert.Equal(0, await WithDbResult(db => db.Refunds.CountAsync(x => x.SaleId == cash.Id)));

        var repeated = await SeedSale(quantity: 2, unitPrice: 10m); var repeatedUrl = $"/api/management/sales/{repeated.Id}/refunds";
        (await client.PostAsJsonAsync(repeatedUrl, new ProcessRefundRequest("first", [new(repeated.LineId, 1)], [new(repeated.PaymentId, 10m)]))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(repeatedUrl, new ProcessRefundRequest("remaining", [new(repeated.LineId, 1)], [new(repeated.PaymentId, 10m)]))).EnsureSuccessStatusCode();
        Assert.Equal(20m, await WithDbResult(db => db.RefundPayments.Where(x => x.Refund.SaleId == repeated.Id).SumAsync(x => x.Amount)));
    }

    [Fact]
    public async Task Split_payments_preserve_each_original_method_and_financial_record()
    {
        var sale = await SeedSplitPaymentSale(); using var client = await Auth("manager@example.com");
        var response = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds", new ProcessRefundRequest("split", [new(sale.LineId, 1)], [new(sale.CashId, 4m), new(sale.CardId, 6m, " card-ref ")]));
        response.EnsureSuccessStatusCode(); var refund = (await response.Content.ReadFromJsonAsync<RefundResponse>())!;
        Assert.Equal([PaymentMethod.Cash, PaymentMethod.Card], refund.Payments.OrderBy(x => x.Id).Select(x => x.Method));
        var originals = await WithDbResult(db => db.Payments.AsNoTracking().Where(x => x.SaleId == sale.Id).OrderBy(x => x.Id).ToListAsync());
        Assert.Equal((4m, 10m, 6m, PaymentStatus.Completed), (originals[0].AmountApplied, originals[0].TenderedAmount, originals[0].ChangeAmount, originals[0].Status));
        Assert.Equal((6m, 6m, 0m, PaymentStatus.Completed), (originals[1].AmountApplied, originals[1].TenderedAmount, originals[1].ChangeAmount, originals[1].Status));
    }

    [Fact]
    public async Task Zero_total_refund_needs_no_payment_and_completes_by_quantity()
    {
        var sale = await SeedRealisticZeroTotalSale(); using var client = await Auth("admin@example.com");
        var response = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest("free item", [new(sale.LineId, 1)], []));
        response.EnsureSuccessStatusCode(); var refund = (await response.Content.ReadFromJsonAsync<RefundResponse>())!;
        Assert.Equal(0m, refund.TotalAmount); Assert.Empty(refund.Payments);
        Assert.Equal(SaleStatus.Refunded, await WithDbResult(db => db.Sales.Where(x => x.Id == sale.Id).Select(x => x.Status).SingleAsync()));
        Assert.Equal(0, await WithDbResult(db => db.Payments.CountAsync(x => x.SaleId == sale.Id)));
        Assert.Equal(0, await WithDbResult(db => db.RefundPayments.CountAsync(x => x.Refund.SaleId == sale.Id)));
    }

    [Fact]
    public async Task Validation_and_atomic_allocation_failures_create_no_artifacts()
    {
        var sale = await SeedSale(); using var client = await Auth("admin@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("   "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest("x", [new(sale.LineId, 1), new(sale.LineId, 1)], [new(sale.PaymentId, 10m)]))).StatusCode);
        var mismatch = await client.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds",
            new ProcessRefundRequest("x", [new(sale.LineId, 1)], [new(sale.PaymentId, 9m)]));
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal(0, await WithDbResult(db => db.Refunds.CountAsync(x => x.SaleId == sale.Id)));
        Assert.Equal(SaleStatus.Completed, await WithDbResult(db => db.Sales.Where(x => x.Id == sale.Id).Select(x => x.Status).SingleAsync()));
    }

    [Fact]
    public async Task Representative_business_failures_are_fully_atomic()
    {
        var sale = await SeedSale(); var foreign = await SeedSale(); using var client = await Auth("admin@example.com"); var url = $"/api/management/sales/{sale.Id}/refunds";
        var baseline = await Counts(sale.Id);
        ProcessRefundRequest[] failures = [
            new("quantity", [new(sale.LineId, 2)], [new(sale.PaymentId, 20m)]),
            new("allocation", [new(sale.LineId, 1)], [new(sale.PaymentId, 9m)]),
            new("wrong payment", [new(sale.LineId, 1)], [new(foreign.PaymentId, 10m)]) ];
        foreach (var failure in failures)
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, failure)).StatusCode);
            Assert.Equal(baseline, await Counts(sale.Id)); Assert.Equal(SaleStatus.Completed, await SaleStatusOf(sale.Id));
        }
    }

    [Fact]
    public async Task Refund_history_is_deterministic_complete_and_hides_internal_fields()
    {
        var sale = await SeedSale(quantity: 2); using var client = await Auth("manager@example.com"); var url = $"/api/management/sales/{sale.Id}/refunds";
        (await client.PostAsJsonAsync(url, new ProcessRefundRequest("first", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m, " first-ref ")]))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(url, new ProcessRefundRequest("second", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m, " second-ref ")]))).EnsureSuccessStatusCode();
        var response = await client.GetAsync(url); response.EnsureSuccessStatusCode(); var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rowVersion", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("concurrency", json, StringComparison.OrdinalIgnoreCase);
        var history = (await response.Content.ReadFromJsonAsync<List<RefundResponse>>())!; Assert.Equal(2, history.Count);
        Assert.True(history.SequenceEqual(history.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)));
        Assert.All(history, x => { Assert.Equal("Manager User", x.ProcessedByName); Assert.True(x.ProcessedByUserId > 0); Assert.Equal("Product", Assert.Single(x.Lines).ProductName); Assert.Equal(PaymentMethod.Cash, Assert.Single(x.Payments).Method); });
        Assert.Equal(["first-ref", "second-ref"], history.Select(x => Assert.Single(x.Payments).ExternalReference));
    }

    [Fact]
    public async Task Competing_refunds_cannot_over_refund_quantity_or_payment_capacity()
    {
        var sale = await SeedSale(); using var first = await Auth("admin@example.com"); using var second = await Auth("manager@example.com");
        var request = new ProcessRefundRequest("race", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m)]);
        var coordinator = factory.Services.GetRequiredService<SaleMutationSaveCoordinator>(); coordinator.Enable();
        HttpResponseMessage[] responses;
        try { responses = await Task.WhenAll(first.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds", request), second.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds", request)); }
        finally { coordinator.Disable(); }
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await WithDbResult(db => db.Refunds.CountAsync(x => x.SaleId == sale.Id)));
    }

    [Fact]
    public async Task Refund_and_void_race_have_one_lifecycle_winner()
    {
        var sale = await SeedSale(); using var first = await Auth("admin@example.com"); using var second = await Auth("manager@example.com");
        var coordinator = factory.Services.GetRequiredService<SaleMutationSaveCoordinator>(); coordinator.Enable();
        HttpResponseMessage[] responses;
        try
        {
            responses = await Task.WhenAll(
                first.PostAsJsonAsync($"/api/management/sales/{sale.Id}/refunds", new ProcessRefundRequest("refund", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m)])),
                second.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("void")));
        }
        finally { coordinator.Disable(); }
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        var state = await WithDbResult(db => db.Sales.Where(x => x.Id == sale.Id).Select(x => new { x.Status, Refunds = db.Refunds.Count(r => r.SaleId == sale.Id) }).SingleAsync());
        Assert.True(state is { Status: SaleStatus.Voided, Refunds: 0 } or { Status: SaleStatus.Refunded, Refunds: 1 });
    }

    [Fact]
    public async Task Concurrent_double_void_has_exactly_one_winner()
    {
        var sale = await SeedSale(); using var first = await Auth("admin@example.com"); using var second = await Auth("manager@example.com");
        var coordinator = factory.Services.GetRequiredService<SaleMutationSaveCoordinator>(); coordinator.Enable();
        HttpResponseMessage[] responses;
        try { responses = await Task.WhenAll(first.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("one")), second.PostAsJsonAsync($"/api/management/sales/{sale.Id}/void", new VoidSaleRequest("two"))); }
        finally { coordinator.Disable(); }
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK)); Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(SaleStatus.Voided, await SaleStatusOf(sale.Id));
    }

    static ProcessRefundRequest RefundRequest((int Id, int LineId, int PaymentId, int ProductId, string Receipt) sale) => new("reason", [new(sale.LineId, 1)], [new(sale.PaymentId, 10m)]);
    async Task SetStatus(int saleId, SaleStatus status) => await WithDb(async db => { var sale = await db.Sales.SingleAsync(x => x.Id == saleId); sale.Status = status; await db.SaveChangesAsync(); });
    async Task<SaleStatus> SaleStatusOf(int saleId) => await WithDbResult(db => db.Sales.Where(x => x.Id == saleId).Select(x => x.Status).SingleAsync());
    async Task<(int Refunds, int Lines, int Payments)> Counts(int saleId) => await WithDbResult(async db => (
        await db.Refunds.CountAsync(x => x.SaleId == saleId), await db.RefundLines.CountAsync(x => x.Refund.SaleId == saleId), await db.RefundPayments.CountAsync(x => x.Refund.SaleId == saleId)));

    async Task SeedCompletedRefund(int saleId, int lineId, int quantity, int paymentId, decimal amount) => await WithDb(async db =>
    {
        var now = DateTimeOffset.UtcNow; var adminId = await db.Users.Where(x => x.Email == "admin@example.com").Select(x => x.Id).SingleAsync();
        db.Refunds.Add(new Refund { SaleId = saleId, ProcessedByUserId = adminId, Status = RefundStatus.Completed, Subtotal = amount, DiscountTotal = 0, TaxTotal = 0, TotalAmount = amount,
            Reason = "historical", CreatedAtUtc = now, UpdatedAtUtc = now, Lines = [new RefundLine { SaleLineId = lineId, Quantity = quantity, Subtotal = amount, DiscountTotal = 0, TaxTotal = 0, TotalAmount = amount }],
            Payments = [new RefundPayment { OriginalPaymentId = paymentId, Method = PaymentMethod.Cash, Amount = amount, CreatedAtUtc = now }] });
        await db.SaveChangesAsync();
    });

    async Task<(int Id, int FirstLineId, int SecondLineId, int PaymentId)> SeedTwoLineSale()
    {
        var first = await SeedSale();
        return await WithDbResult(async db =>
        {
            var sale = await db.Sales.Include(x => x.Lines).Include(x => x.Payments).SingleAsync(x => x.Id == first.Id); var original = sale.Lines.Single(); var now = DateTimeOffset.UtcNow;
            var tax = new TaxRate { Name = "Second tax", Percentage = 0, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var product = new Product { Sku = Guid.NewGuid().ToString("N"), Name = "Second product", UnitPrice = 15m, TaxRate = tax, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var line = new SaleLine { Sale = sale, Product = product, ProductSku = product.Sku, ProductName = product.Name, Quantity = 1, UnitPrice = 15m, UnitNetAmount = 15m,
                TaxRate = tax, TaxRateName = tax.Name, UnitTotal = 15m, LineSubtotal = 15m, LineTotal = 15m };
            sale.Subtotal = sale.TotalAmount = 25m; sale.Payments.Single().AmountApplied = 25m; sale.Payments.Single().TenderedAmount = 30m; sale.Payments.Single().ChangeAmount = 5m;
            db.Add(line); await db.SaveChangesAsync(); return (sale.Id, original.Id, line.Id, sale.Payments.Single().Id);
        });
    }

    async Task<(int Id, int LineId, int PaymentId, int ProductId, int TaxId, int DiscountId, string Sku)> SeedHistoricalSale()
    {
        return await WithDbResult(async db =>
        {
            var now = DateTimeOffset.UtcNow; var cashier = await db.Users.SingleAsync(x => x.Email == "cashier@example.com");
            var branch = new Branch { Name = "Branch", Code = Guid.NewGuid().ToString("N"), Address = "Address", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var register = new Register { Branch = branch, Name = "Register", Code = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var shift = new CashierShift { Branch = branch, Register = register, CashierUser = cashier, Status = CashierShiftStatus.Closed, OpeningFloat = 0, OpenedAtUtc = now, ClosedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            var tax = new TaxRate { Name = "Historical tax", Percentage = 23.75m, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var discount = new Discount { Name = "Historical discount", Type = DiscountType.FixedAmount, Value = 2m, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var sku = Guid.NewGuid().ToString("N"); var product = new Product { Sku = sku, Name = "Historical product", UnitPrice = 10m, TaxRate = tax, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var sale = new Sale { ReceiptNumber = $"R-{Guid.NewGuid():N}", Branch = branch, Register = register, CashierShift = shift, CashierUser = cashier, Status = SaleStatus.Completed,
                Subtotal = 30m, DiscountTotal = 6m, TaxTotal = 5.70m, TotalAmount = 29.70m, CompletedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            var line = new SaleLine { Sale = sale, Product = product, ProductSku = sku, ProductName = "Historical product", Quantity = 3, UnitPrice = 10m, Discount = discount,
                DiscountName = discount.Name, DiscountType = discount.Type, DiscountValue = discount.Value, UnitDiscountAmount = 2m, UnitNetAmount = 8m, TaxRate = tax,
                TaxRateName = tax.Name, TaxRatePercentage = 23.75m, UnitTaxAmount = 1.90m, UnitTotal = 9.90m, LineSubtotal = 30m, LineDiscountTotal = 6m, LineTaxTotal = 5.70m, LineTotal = 29.70m };
            var payment = new Payment { Sale = sale, Method = PaymentMethod.Card, AmountApplied = 29.70m, TenderedAmount = 29.70m, ChangeAmount = 0, Status = PaymentStatus.Completed, CreatedAtUtc = now };
            db.AddRange(line, payment); await db.SaveChangesAsync(); return (sale.Id, line.Id, payment.Id, product.Id, tax.Id, discount.Id, sku);
        });
    }

    async Task<(int Id, int LineId)> SeedRealisticZeroTotalSale()
    {
        var seeded = await SeedSale(unitPrice: 10m);
        await WithDb(async db =>
        {
            var sale = await db.Sales.Include(x => x.Lines).Include(x => x.Payments).SingleAsync(x => x.Id == seeded.Id); var line = sale.Lines.Single();
            line.UnitDiscountAmount = line.UnitPrice; line.UnitNetAmount = 0; line.UnitTaxAmount = 0; line.UnitTotal = 0;
            line.LineDiscountTotal = line.LineSubtotal; line.LineTaxTotal = 0; line.LineTotal = 0; sale.DiscountTotal = sale.Subtotal; sale.TaxTotal = 0; sale.TotalAmount = 0;
            db.Payments.RemoveRange(sale.Payments); await db.SaveChangesAsync();
        });
        return (seeded.Id, seeded.LineId);
    }

    async Task<(int Id, int LineId, int CashId, int CardId)> SeedSplitPaymentSale()
    {
        var seeded = await SeedSale();
        return await WithDbResult(async db =>
        {
            var sale = await db.Sales.Include(x => x.Payments).SingleAsync(x => x.Id == seeded.Id); var cash = sale.Payments.Single();
            cash.AmountApplied = 4m; cash.TenderedAmount = 10m; cash.ChangeAmount = 6m;
            var card = new Payment { Sale = sale, Method = PaymentMethod.Card, AmountApplied = 6m, TenderedAmount = 6m, ChangeAmount = 0, Status = PaymentStatus.Completed, CreatedAtUtc = cash.CreatedAtUtc };
            db.Add(card); await db.SaveChangesAsync(); return (sale.Id, seeded.LineId, cash.Id, card.Id);
        });
    }

    async Task<(int Id, int LineId, int PaymentId, int ProductId, string Receipt)> SeedSale(int quantity = 1, decimal unitPrice = 10m)
    {
        return await WithDbResult(async db =>
        {
            var now = DateTimeOffset.UtcNow; var cashier = await db.Users.SingleAsync(x => x.Email == "cashier@example.com");
            var branch = new Branch { Name = "Branch", Code = Guid.NewGuid().ToString("N"), Address = "Address", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var register = new Register { Branch = branch, Name = "Register", Code = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var shift = new CashierShift { Branch = branch, Register = register, CashierUser = cashier, Status = CashierShiftStatus.Closed, OpeningFloat = 0, OpenedAtUtc = now, ClosedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            var tax = new TaxRate { Name = "Tax", Percentage = 0, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var product = new Product { Sku = Guid.NewGuid().ToString("N"), Name = "Product", UnitPrice = unitPrice, TaxRate = tax, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var receipt = $"R-{Guid.NewGuid():N}"; var total = unitPrice * quantity;
            var sale = new Sale { ReceiptNumber = receipt, Branch = branch, Register = register, CashierShift = shift, CashierUser = cashier,
                Status = SaleStatus.Completed, Subtotal = total, DiscountTotal = 0, TaxTotal = 0, TotalAmount = total,
                CompletedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            var line = new SaleLine { Sale = sale, Product = product, ProductSku = product.Sku, ProductName = product.Name, Quantity = quantity,
                UnitPrice = unitPrice, UnitDiscountAmount = 0, UnitNetAmount = unitPrice, TaxRate = tax, TaxRateName = tax.Name,
                TaxRatePercentage = 0, UnitTaxAmount = 0, UnitTotal = unitPrice, LineSubtotal = total, LineDiscountTotal = 0, LineTaxTotal = 0, LineTotal = total };
            var payment = new Payment { Sale = sale, Method = PaymentMethod.Cash, AmountApplied = total, TenderedAmount = total + 5m,
                ChangeAmount = 5m, Status = PaymentStatus.Completed, CreatedAtUtc = now };
            db.AddRange(line, payment); await db.SaveChangesAsync(); return (sale.Id, line.Id, payment.Id, product.Id, receipt);
        });
    }

    async Task<HttpClient> Auth(string email) { var client = factory.CreateClient(); var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Valid1!Password")); response.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken); return client; }
    async Task WithDb(Func<AppDbContext, Task> action) { using var scope = factory.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<AppDbContext>()); }
    async Task<T> WithDbResult<T>(Func<AppDbContext, Task<T>> action) { using var scope = factory.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>()); }
}
