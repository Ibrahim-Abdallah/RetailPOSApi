using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Tests;

public sealed class PosConfigurationCorrectiveTests : IClassFixture<RetailApiFactory>
{
    private readonly RetailApiFactory _factory;

    public PosConfigurationCorrectiveTests(RetailApiFactory factory) => _factory = factory;

    [Fact]
    public async Task CreatedAt_sort_uses_timestamp_before_id()
    {
        var marker = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Branches.AddRange(
                NewBranch($"first-id-{marker}", now.AddHours(2)),
                NewBranch($"second-id-{marker}", now.AddHours(-2)));
            await db.SaveChangesAsync();
        }

        using var admin = await CreateAuthenticatedClientAsync();
        var ascending = await admin.GetFromJsonAsync<PagedResponse<BranchResponse>>(
            $"/api/admin/branches?search={marker}&sortBy=createdAt&sortDirection=asc");
        var descending = await admin.GetFromJsonAsync<PagedResponse<BranchResponse>>(
            $"/api/admin/branches?search={marker}&sortBy=createdAt&sortDirection=desc");

        Assert.Equal($"second-id-{marker}", ascending!.Items[0].Code);
        Assert.Equal($"first-id-{marker}", descending!.Items[0].Code);
    }

    [Fact]
    public async Task Percentage_values_are_normalized_before_response_and_persistence()
    {
        using var admin = await CreateAuthenticatedClientAsync();
        var tax = await CreateAsync<TaxRateResponse>(admin, "/api/admin/tax-rates",
            new TaxRateRequest("Precise tax", 12.34565m));
        var discount = await CreateAsync<DiscountResponse>(admin, "/api/admin/discounts",
            new DiscountRequest("Precise discount", DiscountType.Percentage, 23.45675m));

        Assert.Equal(12.3457m, tax.Percentage);
        Assert.Equal(23.4568m, discount.Value);
        Assert.Equal(tax.Percentage,
            (await admin.GetFromJsonAsync<TaxRateResponse>($"/api/admin/tax-rates/{tax.Id}"))!.Percentage);
        Assert.Equal(discount.Value,
            (await admin.GetFromJsonAsync<DiscountResponse>($"/api/admin/discounts/{discount.Id}"))!.Value);

        var updated = await (await admin.PutAsJsonAsync($"/api/admin/tax-rates/{tax.Id}",
            new TaxRateRequest("Updated tax", 1.23445m))).Content.ReadFromJsonAsync<TaxRateResponse>();
        Assert.Equal(1.2345m, updated!.Percentage);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1.2345m,
            (await db.TaxRates.AsNoTracking().SingleAsync(item => item.Id == tax.Id)).Percentage);
        Assert.Equal(23.4568m,
            (await db.Discounts.AsNoTracking().SingleAsync(item => item.Id == discount.Id)).Value);
    }

    [Fact]
    public async Task Validation_uses_trimmed_string_lengths_and_null_barcode_semantics()
    {
        using var admin = await CreateAuthenticatedClientAsync();
        var branch = await CreateAsync<BranchResponse>(admin, "/api/admin/branches",
            new BranchRequest($"  {new string('n', 200)}  ", $"  {Guid.NewGuid():N}  ", $"  {new string('a', 500)}  "));
        var tax = await CreateAsync<TaxRateResponse>(admin, "/api/admin/tax-rates",
            new TaxRateRequest("Boundary tax", 1));
        var product = await CreateAsync<ProductResponse>(admin, "/api/admin/products",
            new ProductRequest($"  {new string('s', 100)}  ", new string(' ', 150),
                $"  {new string('p', 300)}  ", 1, tax.Id));

        Assert.Equal(200, branch.Name.Length);
        Assert.Equal(500, branch.Address.Length);
        Assert.Equal(100, product.Sku.Length);
        Assert.Equal(300, product.Name.Length);
        Assert.Null(product.Barcode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/admin/branches",
                new BranchRequest(new string('n', 201), "code", "address"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/admin/products",
                new ProductRequest("sku", $"  {new string('b', 101)}  ", "name", 1, tax.Id))).StatusCode);
    }

    [Fact]
    public async Task Numeric_precision_boundaries_return_created_or_validation_problem()
    {
        using var admin = await CreateAuthenticatedClientAsync();
        var tax = await CreateAsync<TaxRateResponse>(admin, "/api/admin/tax-rates",
            new TaxRateRequest("Capacity tax", 1));

        var maximumProduct = await admin.PostAsJsonAsync("/api/admin/products",
            new ProductRequest(Guid.NewGuid().ToString("N"), null, "Maximum product",
                9_999_999_999_999_999.99m, tax.Id));
        var excessiveProduct = await admin.PostAsJsonAsync("/api/admin/products",
            new ProductRequest(Guid.NewGuid().ToString("N"), null, "Excessive product",
                10_000_000_000_000_000m, tax.Id));
        var maximumDiscount = await admin.PostAsJsonAsync("/api/admin/discounts",
            new DiscountRequest("Maximum fixed", DiscountType.FixedAmount, 99_999.99m));
        var excessiveDiscount = await admin.PostAsJsonAsync("/api/admin/discounts",
            new DiscountRequest("Excessive fixed", DiscountType.FixedAmount, 100_000m));

        Assert.Equal(HttpStatusCode.Created, maximumProduct.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, excessiveProduct.StatusCode);
        Assert.Equal(HttpStatusCode.Created, maximumDiscount.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, excessiveDiscount.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/branches/2147483647", "Branch not found.")]
    [InlineData("/api/admin/registers/2147483647", "Register not found.")]
    [InlineData("/api/admin/products/2147483647", "Product not found.")]
    [InlineData("/api/admin/tax-rates/2147483647", "Tax rate not found.")]
    [InlineData("/api/admin/discounts/2147483647", "Discount not found.")]
    public async Task Missing_items_return_safe_problem_details(string route, string title)
    {
        using var admin = await CreateAuthenticatedClientAsync();
        var response = await admin.GetAsync(route);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(title, problem!.Title);
    }

    [Theory]
    [InlineData("/api/admin/branches?sortBy=value")]
    [InlineData("/api/admin/tax-rates?sortBy=code")]
    [InlineData("/api/admin/registers?sortDirection=sideways")]
    [InlineData("/api/admin/products?page=0")]
    [InlineData("/api/admin/discounts?pageSize=101")]
    public async Task Invalid_queries_return_validation_problems(string route)
    {
        using var admin = await CreateAuthenticatedClientAsync();
        var response = await admin.GetAsync(route);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    private static Branch NewBranch(string code, DateTimeOffset createdAt) => new()
    {
        Name = code,
        Code = code,
        Address = "Sorting test",
        IsActive = true,
        CreatedAtUtc = createdAt,
        UpdatedAtUtc = createdAt
    };

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin@example.com", "Valid1!Password"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<T> CreateAsync<T>(HttpClient client, string route, object request)
    {
        var response = await client.PostAsJsonAsync(route, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
