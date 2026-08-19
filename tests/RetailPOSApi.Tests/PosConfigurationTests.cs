using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Configuration;

namespace RetailPOSApi.Tests;

public sealed class PosConfigurationTests : IClassFixture<RetailApiFactory>
{
    readonly RetailApiFactory factory;
    readonly HttpClient client;
    public PosConfigurationTests(RetailApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/api/admin/branches")]
    [InlineData("/api/admin/registers")]
    [InlineData("/api/admin/products")]
    [InlineData("/api/admin/tax-rates")]
    [InlineData("/api/admin/discounts")]
    public async Task Configuration_routes_are_admin_only(string route)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
        foreach (var email in new[] {
"manager@example.com", "cashier@example.com"
}) using (await Auth(email)) Assert.Equal(HttpStatusCode.Forbidden, (await (await Auth(email)).GetAsync(route)).StatusCode);
        using var admin = await Auth("admin@example.com");
        var response = await admin.GetAsync(route);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

    }

    [Fact]
    public async Task Branch_crud_normalizes_conflicts_and_paginates()
    {
        using var a = await Auth("admin@example.com");
        var code = $" B-{Guid.NewGuid():N} ";
        var create = await a.PostAsJsonAsync("/api/admin/branches", new BranchRequest(" Main ", code, " Address "));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var b = (await create.Content.ReadFromJsonAsync<BranchResponse>())!;
        Assert.Equal("Main", b.Name);
        Assert.Equal(code.Trim(), b.Code);
        Assert.True(b.IsActive);
        Assert.Equal(b.CreatedAtUtc, b.UpdatedAtUtc);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/admin/branches", new BranchRequest("Other", code.Trim(), "X"))).StatusCode);
        var updated = await (await a.PutAsJsonAsync($"/api/admin/branches/{b.Id}", new BranchRequest("Updated", b.Code, "New"))).Content.ReadFromJsonAsync<BranchResponse>();
        Assert.Equal(b.CreatedAtUtc, updated!.CreatedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await a.PatchAsJsonAsync($"/api/admin/branches/{b.Id}/activation", new ActivationRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync("/api/admin/branches?page=0&pageSize=101&sortBy=oops")).StatusCode);
        var page = await a.GetFromJsonAsync<PagedResponse<BranchResponse>>("/api/admin/branches?page=1&pageSize=1&sortBy=code&sortDirection=asc");
        Assert.Single(page!.Items);
        Assert.True(page.TotalPages >= 1);

    }

    [Fact]
    public async Task Register_enforces_branch_and_scoped_code_rules()
    {
        using var a = await Auth("admin@example.com");
        var b1 = await Branch(a);
        var b2 = await Branch(a);
        var code = $"R-{Guid.NewGuid():N}";
        var r1 = await Create<RegisterResponse>(a, "/api/admin/registers", new CreateRegisterRequest(b1.Id, " Till ", code));
        Assert.Equal(b1.Code, r1.BranchCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/admin/registers", new CreateRegisterRequest(b1.Id, "Other", code))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync("/api/admin/registers", new CreateRegisterRequest(b2.Id, "Other", code))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsJsonAsync("/api/admin/registers", new CreateRegisterRequest(int.MaxValue, "X", "X"))).StatusCode);
        await a.PatchAsJsonAsync($"/api/admin/branches/{b1.Id}/activation", new ActivationRequest(false));
        await a.PatchAsJsonAsync($"/api/admin/registers/{r1.Id}/activation", new ActivationRequest(false));
        Assert.Equal(HttpStatusCode.Conflict, (await a.PatchAsJsonAsync($"/api/admin/registers/{r1.Id}/activation", new ActivationRequest(true))).StatusCode);
        var page = await a.GetFromJsonAsync<PagedResponse<RegisterResponse>>($"/api/admin/registers?branchId={b1.Id}");
        Assert.All(page!.Items, x => Assert.Equal(b1.Id, x.BranchId));

    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Tax_rates_accept_boundaries(decimal percentage)
    {
        using var a = await Auth("admin@example.com");
        var x = await Create<TaxRateResponse>(a, "/api/admin/tax-rates", new TaxRateRequest(" Tax ", percentage));
        Assert.Equal(percentage, x.Percentage);
        Assert.True((await a.PutAsJsonAsync($"/api/admin/tax-rates/{x.Id}", new TaxRateRequest("Changed", percentage))).IsSuccessStatusCode);
        Assert.True((await a.PatchAsJsonAsync($"/api/admin/tax-rates/{x.Id}/activation", new ActivationRequest(false))).IsSuccessStatusCode);
    }
    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public async Task Tax_rates_reject_out_of_range(decimal percentage)
    {
        using var a = await Auth("admin@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/admin/tax-rates", new TaxRateRequest("Tax", percentage))).StatusCode);
    }

    [Fact]
    public async Task Discounts_validate_type_range_and_round_fixed_money()
    {
        using var a = await Auth("admin@example.com");
        var p = await Create<DiscountResponse>(a, "/api/admin/discounts", new DiscountRequest("Percent", DiscountType.Percentage, 100));
        Assert.Equal(100, p.Value);
        var f = await Create<DiscountResponse>(a, "/api/admin/discounts", new DiscountRequest("Fixed", DiscountType.FixedAmount, 10.005m));
        Assert.Equal(10.01m, f.Value);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/admin/discounts", new DiscountRequest("Bad", DiscountType.Percentage, 100.01m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/admin/discounts", new DiscountRequest("Bad", (DiscountType)999, 1))).StatusCode);
    }

    [Fact]
    public async Task Products_enforce_tax_uniqueness_normalization_rounding_and_filters()
    {
        using var a = await Auth("admin@example.com");
        var tax = await Create<TaxRateResponse>(a, "/api/admin/tax-rates", new TaxRateRequest("VAT", 14));
        var sku = $"S-{Guid.NewGuid():N}";
        var p = await Create<ProductResponse>(a, "/api/admin/products", new ProductRequest($" {sku} ", "   ", " Product ", 10.005m, tax.Id));
        Assert.Equal(sku, p.Sku);
        Assert.Null(p.Barcode);
        Assert.Equal(10.01m, p.UnitPrice);
        Assert.Equal("VAT", p.TaxRateName);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/admin/products", new ProductRequest(sku, null, "Other", 1, tax.Id))).StatusCode);
        var barcode = $"BC-{Guid.NewGuid():N}";
        await Create<ProductResponse>(a, "/api/admin/products", new ProductRequest(Guid.NewGuid().ToString("N"), barcode, "B", 1, tax.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/admin/products", new ProductRequest(Guid.NewGuid().ToString("N"), barcode, "C", 1, tax.Id))).StatusCode);
        var page = await a.GetFromJsonAsync<PagedResponse<ProductResponse>>($"/api/admin/products?search={sku}&taxRateId={tax.Id}&isActive=true&sortBy=unitPrice&sortDirection=asc");
        Assert.Contains(page!.Items, x => x.Id == p.Id);
        await a.PatchAsJsonAsync($"/api/admin/products/{p.Id}/activation", new ActivationRequest(false));
        await a.PatchAsJsonAsync($"/api/admin/tax-rates/{tax.Id}/activation", new ActivationRequest(false));
        Assert.Equal(HttpStatusCode.Conflict, (await a.PatchAsJsonAsync($"/api/admin/products/{p.Id}/activation", new ActivationRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/admin/products", new ProductRequest(Guid.NewGuid().ToString("N"), null, "Inactive tax", 1, tax.Id))).StatusCode);

    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isActive\":null}")]
    [InlineData("{")]
    public async Task Activation_requires_explicit_non_null_boolean(string json)
    {
        using var a = await Auth("admin@example.com");
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsync("/api/admin/branches/1/activation", body)).StatusCode);
    }

    async Task<BranchResponse> Branch(HttpClient a) => await Create<BranchResponse>(a, "/api/admin/branches", new BranchRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "Address"));
    static async Task<T> Create<T>(HttpClient c, string route, object value)
    {
        var r = await c.PostAsJsonAsync(route, value);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }
    async Task<HttpClient> Auth(string email)
    {
        var c = factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Valid1!Password"));
        login.EnsureSuccessStatusCode();
        var body = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        return c;
    }
}
