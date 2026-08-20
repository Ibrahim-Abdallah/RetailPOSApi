using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed class SaleCompletionTests : IClassFixture<RetailApiFactory>
{
    readonly RetailApiFactory factory;
    public SaleCompletionTests(RetailApiFactory factory) => this.factory = factory;

    [Theory]
    [InlineData(PaymentMethod.Cash)]
    [InlineData(PaymentMethod.Card)]
    [InlineData(PaymentMethod.Other)]
    public async Task Completes_each_supported_tender_and_exposes_receipt(PaymentMethod method)
    {
        var (client, sale, _) = await ReadySale(100m);
        var response = await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete",
            Request($"key-{Guid.NewGuid():N}", new CompleteSalePaymentRequest(method, 100m, 100m, " ref ")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = (await response.Content.ReadFromJsonAsync<SaleResponse>())!;
        Assert.Equal(SaleStatus.Completed, completed.Status);
        Assert.Matches($"^RCP-[0-9]{{8}}-{sale.Id:D10}$", completed.ReceiptNumber!);
        Assert.NotNull(completed.CompletedAtUtc);
        var payment = Assert.Single(completed.Payments);
        Assert.Equal((method, 100m, 100m, 0m, "ref", PaymentStatus.Completed),
            (payment.Method, payment.AmountApplied, payment.TenderedAmount, payment.ChangeAmount, payment.ExternalReference, payment.Status));
        var read = await client.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}");
        Assert.Equal(completed.ReceiptNumber, read!.ReceiptNumber);
        Assert.Single(read.Payments);
    }

    [Fact]
    public async Task Cash_change_and_split_tender_use_applied_total()
    {
        var (client, sale, _) = await ReadySale(100m);
        var result = await Complete(client, sale.Id, Request("split", new CompleteSalePaymentRequest(PaymentMethod.Cash, 60m, 100m), new CompleteSalePaymentRequest(PaymentMethod.Card, 40m, 40m)));
        Assert.Equal(2, result.Payments.Count);
        Assert.Equal(40m, result.Payments.Single(x => x.Method == PaymentMethod.Cash).ChangeAmount);
        Assert.Equal(0m, result.Payments.Single(x => x.Method == PaymentMethod.Card).ChangeAmount);
    }

    [Theory]
    [InlineData(99, HttpStatusCode.Conflict)]
    [InlineData(101, HttpStatusCode.Conflict)]
    public async Task Allocation_mismatch_is_atomic(decimal applied, HttpStatusCode expected)
    {
        var (client, sale, _) = await ReadySale(100m);
        var response = await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("bad-total", new CompleteSalePaymentRequest(PaymentMethod.Cash, applied, applied)));
        Assert.Equal(expected, response.StatusCode);
        await AssertOpenAndClean(sale.Id);
    }

    [Theory]
    [MemberData(nameof(InvalidPayments))]
    public async Task Invalid_payment_shape_returns_400_without_mutation(CompleteSalePaymentRequest payment)
    {
        var (client, sale, _) = await ReadySale(100m);
        var response = await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("invalid", payment));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertOpenAndClean(sale.Id);
    }

    public static TheoryData<CompleteSalePaymentRequest> InvalidPayments => new()
    {
        new((PaymentMethod)99, 100m, 100m), new(PaymentMethod.Cash, 0m, 0m),
        new(PaymentMethod.Cash, -1m, -1m), new(PaymentMethod.Cash, 100m, 99m),
        new(PaymentMethod.Card, 100m, 101m), new(PaymentMethod.Other, 100m, 99m),
        new(PaymentMethod.Cash, 100.001m, 100.001m),
        new(PaymentMethod.Cash, 100m, 100m, new string('x', 201))
    };

    [Fact]
    public async Task Positive_total_requires_payment_and_empty_sale_cannot_complete()
    {
        var (client, sale, _) = await ReadySale(10m);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("none"))).StatusCode);
        await AssertOpenAndClean(sale.Id);
        var empty = await CreateSale(client);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/cashier/sales/{empty.Id}/complete", Request("empty"))).StatusCode);
        await AssertOpenAndClean(empty.Id);
    }

    [Fact]
    public async Task Zero_total_sale_completes_without_payment_and_rejects_positive_payment()
    {
        var (client, free, _) = await ReadySale(0m);
        var completed = await Complete(client, free.Id, Request("free"));
        Assert.Empty(completed.Payments);
        var (client2, free2, _) = await ReadySale(0m);
        Assert.Equal(HttpStatusCode.Conflict, (await client2.PostAsJsonAsync($"/api/cashier/sales/{free2.Id}/complete", Request("free-paid", new CompleteSalePaymentRequest(PaymentMethod.Cash, 1m, 1m)))).StatusCode);
        await AssertOpenAndClean(free2.Id);
    }

    [Fact]
    public async Task Completion_revalidates_product_but_retains_financial_snapshots()
    {
        var (client, sale, product) = await ReadySale(10m);
        await WithDb(async db => { var p = await db.Products.FindAsync(product.Id); p!.Name = "Changed"; p.UnitPrice = 99m; p.IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("inactive", new CompleteSalePaymentRequest(PaymentMethod.Cash, 10m, 10m)))).StatusCode);
        await AssertOpenAndClean(sale.Id);
        await WithDb(async db => { (await db.Products.FindAsync(product.Id))!.IsActive = true; await db.SaveChangesAsync(); });
        var completed = await Complete(client, sale.Id, Request("active", new CompleteSalePaymentRequest(PaymentMethod.Cash, 10m, 10m)));
        Assert.Equal(("Product", 10m, 10m), (completed.Lines.Single().ProductName, completed.Lines.Single().UnitPrice, completed.TotalAmount));
    }

    [Fact]
    public async Task Replay_is_idempotent_and_conflicting_reuse_is_rejected()
    {
        var (client, sale, _) = await ReadySale(25m);
        var request = Request(" replay-key ", new CompleteSalePaymentRequest(PaymentMethod.Cash, 25m, 30m, " terminal "));
        var first = await Complete(client, sale.Id, request);
        var replay = await Complete(client, sale.Id, Request("replay-key", new CompleteSalePaymentRequest(PaymentMethod.Cash, 25.0m, 30.00m, "terminal")));
        Assert.Equal(first.ReceiptNumber, replay.ReceiptNumber);
        Assert.Equal(first.CompletedAtUtc, replay.CompletedAtUtc);
        Assert.Equal(first.UpdatedAtUtc, replay.UpdatedAtUtc);
        Assert.Equal(first.Payments.Select(x => x.Id), replay.Payments.Select(x => x.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("replay-key", new CompleteSalePaymentRequest(PaymentMethod.Cash, 25m, 31m)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("other-key", new CompleteSalePaymentRequest(PaymentMethod.Cash, 25m, 30m, "terminal")))).StatusCode);
        await WithDb(async db => Assert.Single(await db.Payments.Where(x => x.SaleId == sale.Id).ToListAsync()));
    }

    [Fact]
    public async Task Management_read_exposes_completed_payment_data()
    {
        var (client, sale, _) = await ReadySale(12m);
        await Complete(client, sale.Id, Request("management", new CompleteSalePaymentRequest(PaymentMethod.Other, 12m, 12m)));
        using var manager = await Auth("manager@example.com");
        var read = await manager.GetFromJsonAsync<SaleResponse>($"/api/management/sales/{sale.Id}");
        Assert.NotNull(read!.ReceiptNumber); Assert.NotNull(read.CompletedAtUtc); Assert.Single(read.Payments);
    }

    [Fact]
    public async Task Completion_route_enforces_authentication_roles_and_foreign_ownership()
    {
        var (owner, sale, _) = await ReadySale(15m);
        using var anonymous = factory.CreateClient(); using var manager = await Auth("manager@example.com"); using var admin = await Auth("admin@example.com");
        var request = Request("authorization", new CompleteSalePaymentRequest(PaymentMethod.Cash, 15m, 15m));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", request)).StatusCode);

        var foreign = await CreateCashier(); using var foreignClient = await Auth(foreign.Email);
        var response = await foreignClient.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Sale not found.", (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
        await AssertOpenAndClean(sale.Id);
        owner.Dispose();
    }

    [Theory]
    [InlineData(false, UserRole.Cashier)]
    [InlineData(true, UserRole.Manager)]
    public async Task Previously_issued_cashier_token_rechecks_persisted_account(bool active, UserRole role)
    {
        var cashier = await CreateCashier();
        await SeedShiftAndProduct(cashier.Id, 10m);
        using var client = await Auth(cashier.Email); var sale = await CreateSale(client);
        var productId = await WithDbResult(db => db.Products.OrderByDescending(x => x.Id).Select(x => x.Id).FirstAsync());
        sale = await Read(await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(productId, 1)));
        await WithDb(async db => { var user = await db.Users.FindAsync(cashier.Id); user!.IsActive = active; user.Role = role; await db.SaveChangesAsync(); });
        var response = await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("account-state", new CompleteSalePaymentRequest(PaymentMethod.Cash, 10m, 10m)));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertOpenAndClean(sale.Id);
    }

    [Fact]
    public async Task Closed_or_replaced_original_shift_rejects_completion_atomically()
    {
        var (client, closedSale, _) = await ReadySale(10m);
        await WithDb(async db => { var shift = await db.CashierShifts.FindAsync(closedSale.CashierShiftId); shift!.Status = CashierShiftStatus.Closed; shift.ClosedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/cashier/sales/{closedSale.Id}/complete", Request("closed", new CompleteSalePaymentRequest(PaymentMethod.Cash, 10m, 10m)))).StatusCode);
        await AssertOpenAndClean(closedSale.Id);

        await SeedShiftAndProduct(closedSale.CashierUserId, 1m);
        var mismatch = await client.PostAsJsonAsync($"/api/cashier/sales/{closedSale.Id}/complete", Request("old", new CompleteSalePaymentRequest(PaymentMethod.Cash, 10m, 10m)));
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal("The sale does not belong to the current open cashier shift.", (await mismatch.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
        await AssertOpenAndClean(closedSale.Id);
    }

    [Fact]
    public async Task Tax_and_discount_configuration_changes_and_deactivation_preserve_historical_snapshots()
    {
        await CloseShift();
        int productId = 0, taxId = 0, discountId = 0;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow; var user = await db.Users.SingleAsync(x => x.Email == "cashier@example.com");
            var branch = new Branch { Name = "Branch", Code = Guid.NewGuid().ToString("N"), Address = "Address", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var register = new Register { Branch = branch, Name = "Register", Code = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var shift = new CashierShift { Branch = branch, Register = register, CashierUserId = user.Id, Status = CashierShiftStatus.Open, OpeningFloat = 0, OpenedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            var tax = new TaxRate { Name = "VAT 20", Percentage = 20m, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var product = new Product { Sku = Guid.NewGuid().ToString("N"), Name = "Snapshot product", UnitPrice = 100m, TaxRate = tax, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var discount = new Discount { Name = "Ten percent", Type = DiscountType.Percentage, Value = 10m, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.AddRange(shift, product, discount); await db.SaveChangesAsync(); productId = product.Id; taxId = tax.Id; discountId = discount.Id;
        });
        using var client = await Auth("cashier@example.com"); var sale = await CreateSale(client);
        sale = await Read(await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(productId, 1, discountId)));
        var before = Assert.Single(sale.Lines); Assert.Equal(108m, sale.TotalAmount);
        await WithDb(async db =>
        {
            var tax = await db.TaxRates.FindAsync(taxId); tax!.Name = "Changed tax"; tax.Percentage = 5m; tax.IsActive = false;
            var discount = await db.Discounts.FindAsync(discountId); discount!.Name = "Changed discount"; discount.Type = DiscountType.FixedAmount; discount.Value = 99m; discount.IsActive = false;
            await db.SaveChangesAsync();
        });
        var completed = await Complete(client, sale.Id, Request("historical-config", new CompleteSalePaymentRequest(PaymentMethod.Card, 108m, 108m)));
        var after = Assert.Single(completed.Lines);
        Assert.Equal((before.ProductName, before.UnitPrice, before.TaxRateName, before.TaxRatePercentage, before.DiscountName, before.DiscountType, before.DiscountValue),
            (after.ProductName, after.UnitPrice, after.TaxRateName, after.TaxRatePercentage, after.DiscountName, after.DiscountType, after.DiscountValue));
        Assert.Equal((before.UnitDiscountAmount, before.UnitNetAmount, before.UnitTaxAmount, before.UnitTotal, before.LineTotal),
            (after.UnitDiscountAmount, after.UnitNetAmount, after.UnitTaxAmount, after.UnitTotal, after.LineTotal));
        Assert.Equal(108m, completed.TotalAmount);
    }

    [Fact]
    public async Task Completion_recalculates_stale_sale_totals_and_compares_payments_to_authoritative_lines()
    {
        var (client, succeeds, _) = await ReadySale(30m);
        await SetStaleTotals(succeeds.Id, 10m);
        var completed = await Complete(client, succeeds.Id, Request("authoritative", new CompleteSalePaymentRequest(PaymentMethod.Cash, 30m, 30m)));
        Assert.Equal(30m, completed.TotalAmount);
        await WithDb(async db => { var persisted = await db.Sales.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == succeeds.Id); Assert.Equal(persisted.Lines.Sum(x => x.LineTotal), persisted.TotalAmount); });

        var (client2, rejected, _) = await ReadySale(30m);
        await SetStaleTotals(rejected.Id, 10m);
        var response = await client2.PostAsJsonAsync($"/api/cashier/sales/{rejected.Id}/complete", Request("stale-allocation", new CompleteSalePaymentRequest(PaymentMethod.Cash, 10m, 10m)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertOpenAndClean(rejected.Id, expectedTotal: 10m);
    }

    [Fact]
    public async Task Successful_completion_persists_normalized_metadata_payments_and_invariant_unique_receipts()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-EG");
            var (client, firstSale, product) = await ReadySale(100m);
            var completed = await Complete(client, firstSale.Id, Request(" persisted-key ",
                new CompleteSalePaymentRequest(PaymentMethod.Cash, 60m, 100m, " cash-ref "),
                new CompleteSalePaymentRequest(PaymentMethod.Card, 40m, 40m, " card-ref ")));
            await WithDb(async db =>
            {
                var sale = await db.Sales.AsNoTracking().Include(x => x.Payments).SingleAsync(x => x.Id == firstSale.Id);
                Assert.Equal(SaleStatus.Completed, sale.Status); Assert.Equal(completed.ReceiptNumber, sale.ReceiptNumber);
                Assert.Equal(completed.CompletedAtUtc, sale.CompletedAtUtc); Assert.Equal(sale.CompletedAtUtc, sale.UpdatedAtUtc);
                Assert.Equal("persisted-key", sale.CompletionIdempotencyKey);
                Assert.Matches("^[0-9a-f]{64}$", sale.CompletionRequestHash!);
                Assert.Equal(2, sale.Payments.Count); Assert.All(sale.Payments, x => { Assert.Equal(PaymentStatus.Completed, x.Status); Assert.Equal(sale.CompletedAtUtc, x.CreatedAtUtc); });
                var cash = sale.Payments.Single(x => x.Method == PaymentMethod.Cash); Assert.Equal((60m, 100m, 40m, "cash-ref"), (cash.AmountApplied, cash.TenderedAmount, cash.ChangeAmount, cash.ExternalReference));
                var card = sale.Payments.Single(x => x.Method == PaymentMethod.Card); Assert.Equal((40m, 40m, 0m, "card-ref"), (card.AmountApplied, card.TenderedAmount, card.ChangeAmount, card.ExternalReference));
            });
            Assert.Equal($"RCP-{completed.CompletedAtUtc:yyyyMMdd}-{firstSale.Id:D10}", completed.ReceiptNumber);
            var raw = await client.GetStringAsync($"/api/cashier/sales/{firstSale.Id}");
            Assert.DoesNotContain("completionIdempotencyKey", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("completionRequestHash", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rowVersion", raw, StringComparison.OrdinalIgnoreCase);

            var secondSale = await CreateSale(client);
            secondSale = await Read(await client.PostAsJsonAsync($"/api/cashier/sales/{secondSale.Id}/lines", new AddSaleLineRequest(product.Id, 1)));
            var second = await Complete(client, secondSale.Id, Request("second-receipt", new CompleteSalePaymentRequest(PaymentMethod.Card, 100m, 100m)));
            Assert.NotEqual(completed.ReceiptNumber, second.ReceiptNumber);
        }
        finally { CultureInfo.CurrentCulture = priorCulture; }
    }

    [Fact]
    public async Task Completed_sale_rejects_every_building_mutation_without_changing_history()
    {
        var (client, sale, product) = await ReadySale(50m); var line = Assert.Single(sale.Lines);
        var completed = await Complete(client, sale.Id, Request("immutable", new CompleteSalePaymentRequest(PaymentMethod.Cash, 50m, 75m)));
        int discountId = 0;
        await WithDb(async db => { var now = DateTimeOffset.UtcNow; var discount = new Discount { Name = "Discount", Type = DiscountType.Percentage, Value = 10m, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now }; db.Add(discount); await db.SaveChangesAsync(); discountId = discount.Id; });
        var attempts = new[]
        {
            await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1)),
            await client.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}", new UpdateSaleLineQuantityRequest(2)),
            await client.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount", new ApplySaleLineDiscountRequest(discountId)),
            await client.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}/discount"),
            await client.DeleteAsync($"/api/cashier/sales/{sale.Id}/lines/{line.Id}")
        };
        Assert.All(attempts, x => Assert.Equal(HttpStatusCode.Conflict, x.StatusCode));
        var after = await client.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}");
        AssertSaleEquivalent(completed, after!);
        await WithDb(async db => { var persisted = await db.Sales.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments).SingleAsync(x => x.Id == sale.Id); Assert.Single(persisted.Lines); Assert.Single(persisted.Payments); Assert.Equal(completed.ReceiptNumber, persisted.ReceiptNumber); Assert.Equal(completed.CompletedAtUtc, persisted.CompletedAtUtc); });
    }

    [Fact]
    public async Task Payment_decimal_capacity_is_validated_and_maximum_boundary_can_complete()
    {
        var above = SaleCalculation.MaximumMoney + 0.01m;
        var (client, sale, _) = await ReadySale(100m);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("amount-capacity", new CompleteSalePaymentRequest(PaymentMethod.Cash, above, above)))).StatusCode);
        await AssertOpenAndClean(sale.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request("tender-capacity", new CompleteSalePaymentRequest(PaymentMethod.Cash, 100m, above)))).StatusCode);
        await AssertOpenAndClean(sale.Id);

        var (boundaryClient, boundarySale, _) = await ReadySale(100m);
        var boundaryResponse = await boundaryClient.PostAsJsonAsync($"/api/cashier/sales/{boundarySale.Id}/complete", Request("maximum", new CompleteSalePaymentRequest(PaymentMethod.Cash, 100m, SaleCalculation.MaximumMoney)));
        Assert.True(boundaryResponse.IsSuccessStatusCode, await boundaryResponse.Content.ReadAsStringAsync());
        var completed = (await boundaryResponse.Content.ReadFromJsonAsync<SaleResponse>())!;
        Assert.Equal(100m, completed.TotalAmount); Assert.Single(completed.Payments);
    }

    [Fact]
    public async Task Open_and_completed_reads_are_safe_and_consistent_for_cashier_and_management()
    {
        var (client, sale, _) = await ReadySale(18m);
        var open = await client.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}");
        Assert.Null(open!.ReceiptNumber); Assert.Null(open.CompletedAtUtc); Assert.Empty(open.Payments);
        var completed = await Complete(client, sale.Id, Request("read-model", new CompleteSalePaymentRequest(PaymentMethod.Other, 18m, 18m, "safe-ref")));
        var own = await client.GetFromJsonAsync<SaleResponse>($"/api/cashier/sales/{sale.Id}"); using var manager = await Auth("manager@example.com");
        var management = await manager.GetFromJsonAsync<SaleResponse>($"/api/management/sales/{sale.Id}");
        AssertSaleEquivalent(completed, own!); AssertSaleEquivalent(completed, management!); Assert.Equal("safe-ref", Assert.Single(own!.Payments).ExternalReference);
    }

    [Fact]
    public async Task Concurrent_identical_requests_are_idempotent_across_ten_iterations()
    {
        var coordinator = factory.Services.GetRequiredService<SaleMutationSaveCoordinator>();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var (first, sale, _) = await ReadySale(20m); using var second = await Auth("cashier@example.com");
            var request = Request($"same-{iteration}", new CompleteSalePaymentRequest(PaymentMethod.Cash, 20m, 25m));
            coordinator.Enable();
            HttpResponseMessage[] responses;
            try
            {
                responses = await Task.WhenAll(
                    first.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", request),
                    second.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", request));
            }
            finally { coordinator.Disable(); }
            Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
            var values = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<SaleResponse>()));
            Assert.Single(values.Select(x => x!.ReceiptNumber).Distinct());
            await WithDb(async db => Assert.Single(await db.Payments.Where(x => x.SaleId == sale.Id).ToListAsync()));
        }
    }

    [Fact]
    public async Task Concurrent_different_requests_have_one_winner_across_ten_iterations()
    {
        var coordinator = factory.Services.GetRequiredService<SaleMutationSaveCoordinator>();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var (first, sale, _) = await ReadySale(20m); using var second = await Auth("cashier@example.com");
            coordinator.Enable();
            HttpResponseMessage[] responses;
            try
            {
                responses = await Task.WhenAll(
                    first.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request($"a-{iteration}", new CompleteSalePaymentRequest(PaymentMethod.Cash, 20m, 20m))),
                    second.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request($"b-{iteration}", new CompleteSalePaymentRequest(PaymentMethod.Card, 20m, 20m))));
            }
            finally { coordinator.Disable(); }
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            await WithDb(async db =>
            {
                var persisted = await db.Sales.AsNoTracking().Include(x => x.Payments).SingleAsync(x => x.Id == sale.Id);
                Assert.Equal(SaleStatus.Completed, persisted.Status); Assert.Single(persisted.Payments);
                Assert.Equal(persisted.TotalAmount, persisted.Payments.Sum(x => x.AmountApplied));
            });
        }
    }

    [Fact]
    public async Task Completion_and_line_mutation_race_preserves_an_authoritative_state_across_ten_iterations()
    {
        var coordinator = factory.Services.GetRequiredService<SaleMutationSaveCoordinator>();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var (first, sale, _) = await ReadySale(20m); using var second = await Auth("cashier@example.com");
            var lineId = Assert.Single(sale.Lines).Id;
            coordinator.Enable();
            HttpResponseMessage[] responses;
            try
            {
                responses = await Task.WhenAll(
                    first.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/complete", Request($"race-{iteration}", new CompleteSalePaymentRequest(PaymentMethod.Cash, 20m, 20m))),
                    second.PutAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines/{lineId}", new UpdateSaleLineQuantityRequest(2)));
            }
            finally { coordinator.Disable(); }
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            await WithDb(async db =>
            {
                var persisted = await db.Sales.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments).SingleAsync(x => x.Id == sale.Id);
                Assert.Equal(persisted.Lines.Sum(x => x.LineTotal), persisted.TotalAmount);
                if (persisted.Status == SaleStatus.Completed)
                {
                    Assert.Single(persisted.Payments);
                    Assert.Equal(persisted.TotalAmount, persisted.Payments.Sum(x => x.AmountApplied));
                }
                else
                {
                    Assert.Equal(SaleStatus.Open, persisted.Status); Assert.Empty(persisted.Payments);
                    Assert.Null(persisted.ReceiptNumber); Assert.Null(persisted.CompletionIdempotencyKey);
                }
            });
        }
    }

    async Task<(HttpClient Client, SaleResponse Sale, Product Product)> ReadySale(decimal price)
    {
        await CloseShift();
        Product product = null!;
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow; var user = await db.Users.SingleAsync(x => x.Email == "cashier@example.com");
            var branch = new Branch { Name = "Branch", Code = Guid.NewGuid().ToString("N"), Address = "Address", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var register = new Register { Branch = branch, Name = "Register", Code = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(new CashierShift { Branch = branch, Register = register, CashierUserId = user.Id, Status = CashierShiftStatus.Open, OpeningFloat = 0, OpenedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
            var tax = new TaxRate { Name = "No tax", Percentage = 0, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            product = new Product { Sku = Guid.NewGuid().ToString("N"), Name = "Product", UnitPrice = price, TaxRate = tax, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(product); await db.SaveChangesAsync();
        });
        var client = await Auth("cashier@example.com");
        var sale = await CreateSale(client);
        sale = await Read(await client.PostAsJsonAsync($"/api/cashier/sales/{sale.Id}/lines", new AddSaleLineRequest(product.Id, 1)));
        return (client, sale, product);
    }

    async Task SeedShiftAndProduct(int cashierId, decimal price)
    {
        await WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var branch = new Branch { Name = "Branch", Code = Guid.NewGuid().ToString("N"), Address = "Address", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var register = new Register { Branch = branch, Name = "Register", Code = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var shift = new CashierShift { Branch = branch, Register = register, CashierUserId = cashierId, Status = CashierShiftStatus.Open, OpeningFloat = 0, OpenedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };
            var tax = new TaxRate { Name = "No tax", Percentage = 0, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            var product = new Product { Sku = Guid.NewGuid().ToString("N"), Name = "Product", UnitPrice = price, TaxRate = tax, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.AddRange(shift, product); await db.SaveChangesAsync();
        });
    }

    async Task<User> CreateCashier()
    {
        User user = null!;
        await WithDb(async db =>
        {
            using var scope = factory.Services.CreateScope(); var now = DateTimeOffset.UtcNow; var email = $"completion-{Guid.NewGuid():N}@example.com";
            user = new User { FirstName = "Foreign", LastName = "Cashier", Email = email, NormalizedEmail = EmailNormalizer.Normalize(email), PasswordHash = "", Role = UserRole.Cashier, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, "Valid1!Password");
            db.Add(user); await db.SaveChangesAsync();
        });
        return user;
    }

    async Task SetStaleTotals(int saleId, decimal total) => await WithDb(async db =>
    {
        var sale = await db.Sales.FindAsync(saleId); sale!.Subtotal = total; sale.DiscountTotal = 0; sale.TaxTotal = 0; sale.TotalAmount = total; await db.SaveChangesAsync();
    });

    async Task CloseShift() => await WithDb(async db =>
    {
        var userId = await db.Users.Where(x => x.Email == "cashier@example.com").Select(x => x.Id).SingleAsync();
        foreach (var shift in await db.CashierShifts.Where(x => x.CashierUserId == userId && x.Status == CashierShiftStatus.Open).ToListAsync()) shift.Status = CashierShiftStatus.Closed;
        await db.SaveChangesAsync();
    });

    async Task AssertOpenAndClean(int saleId, decimal? expectedTotal = null) => await WithDb(async db =>
    {
        var sale = await db.Sales.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == saleId);
        Assert.Equal(SaleStatus.Open, sale.Status); Assert.Null(sale.ReceiptNumber); Assert.Null(sale.CompletedAtUtc);
        Assert.Null(sale.CompletionIdempotencyKey); Assert.Null(sale.CompletionRequestHash);
        Assert.Empty(await db.Payments.Where(x => x.SaleId == saleId).ToListAsync());
        if (expectedTotal.HasValue) Assert.Equal(expectedTotal.Value, sale.TotalAmount);
    });

    static CompleteSaleRequest Request(string key, params CompleteSalePaymentRequest[] payments) => new(key, payments);
    static void AssertSaleEquivalent(SaleResponse expected, SaleResponse actual)
    {
        Assert.Equal((expected.Id, expected.Status, expected.Subtotal, expected.DiscountTotal, expected.TaxTotal, expected.TotalAmount,
            expected.ReceiptNumber, expected.CompletedAtUtc, expected.CreatedAtUtc, expected.UpdatedAtUtc),
            (actual.Id, actual.Status, actual.Subtotal, actual.DiscountTotal, actual.TaxTotal, actual.TotalAmount,
            actual.ReceiptNumber, actual.CompletedAtUtc, actual.CreatedAtUtc, actual.UpdatedAtUtc));
        Assert.Equal(expected.Lines.ToArray(), actual.Lines.ToArray());
        Assert.Equal(expected.Payments.ToArray(), actual.Payments.ToArray());
    }
    static async Task<SaleResponse> Complete(HttpClient client, int id, CompleteSaleRequest request) => await Read(await client.PostAsJsonAsync($"/api/cashier/sales/{id}/complete", request));
    static async Task<SaleResponse> CreateSale(HttpClient client) => await Read(await client.PostAsync("/api/cashier/sales", null));
    static async Task<SaleResponse> Read(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<SaleResponse>())!; }
    async Task<HttpClient> Auth(string email)
    {
        var client = factory.CreateClient(); var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Valid1!Password")); login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken); return client;
    }
    async Task WithDb(Func<AppDbContext, Task> action) { using var scope = factory.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<AppDbContext>()); }
    async Task<T> WithDbResult<T>(Func<AppDbContext, Task<T>> action) { using var scope = factory.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>()); }
}
