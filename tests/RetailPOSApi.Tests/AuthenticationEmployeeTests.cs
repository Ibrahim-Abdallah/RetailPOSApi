using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Employees;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Tests;

public sealed class AuthenticationEmployeeTests : IClassFixture<RetailApiFactory>
{
    private readonly RetailApiFactory _factory;
    private readonly HttpClient _client;
    public AuthenticationEmployeeTests(RetailApiFactory factory) { _factory = factory; _client = factory.CreateClient(); }

    [Theory]
    [InlineData("admin@example.com", "Admin")]
    [InlineData("manager@example.com", "Manager")]
    [InlineData("cashier@example.com", "Cashier")]
    public async Task Active_roles_can_login(string email, string role)
    {
        var response = await Login(email);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal(role, body!.Employee.Role.ToString());
        Assert.NotEmpty(body.AccessToken);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.AccessToken);
        Assert.Equal(body.Employee.Id.ToString(), jwt.Subject);
        Assert.Equal(email, jwt.Claims.Single(x => x.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal(role, jwt.Claims.Single(x => x.Type == ClaimTypes.Role).Value);
        Assert.DoesNotContain("passwordHash", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown@example.com", "Valid1!Password")]
    [InlineData("admin@example.com", "wrong")]
    [InlineData("inactive@example.com", "Valid1!Password")]
    public async Task Invalid_credentials_are_generic_unauthorized(string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid credentials", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_normalizes_email_case_and_whitespace() => Assert.True((await Login("  ADMIN@EXAMPLE.COM ")).IsSuccessStatusCode);

    [Fact]
    public async Task Anonymous_manager_and_cashier_cannot_create_employee()
    {
        var request = NewEmployee("blocked@example.com", UserRole.Cashier);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/admin/employees", request)).StatusCode);
        foreach (var email in new[] { "manager@example.com", "cashier@example.com" })
        {
            using var client = await AuthenticatedClient(email);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/employees", request)).StatusCode);
        }
    }

    [Fact]
    public async Task Admin_can_create_query_and_activate_employee()
    {
        using var client = await AuthenticatedClient("admin@example.com");
        var create = await client.PostAsJsonAsync("/api/admin/employees", NewEmployee("  NEW@Example.com ", UserRole.Manager));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var employee = await create.Content.ReadFromJsonAsync<EmployeeSummary>();
        Assert.True(employee!.IsActive); Assert.Equal(UserRole.Manager, employee.Role);
        var list = await client.GetAsync("/api/admin/employees?pageNumber=1&pageSize=2");
        Assert.True(list.IsSuccessStatusCode, await list.Content.ReadAsStringAsync());
        Assert.True((await client.GetAsync($"/api/admin/employees/{employee.Id}")).IsSuccessStatusCode);
        Assert.True((await client.PatchAsJsonAsync($"/api/admin/employees/{employee.Id}/activation", new ActivationRequest(false))).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login("new@example.com")).StatusCode);
        await client.PatchAsJsonAsync($"/api/admin/employees/{employee.Id}/activation", new ActivationRequest(true));
        Assert.True((await Login("new@example.com")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Activation_requires_explicit_is_active_and_accepts_both_boolean_values()
    {
        using var client = await AuthenticatedClient("admin@example.com");
        var create = await client.PostAsJsonAsync("/api/admin/employees", NewEmployee($"activation-{Guid.NewGuid():N}@example.com", UserRole.Cashier));
        create.EnsureSuccessStatusCode();
        var employee = await create.Content.ReadFromJsonAsync<EmployeeSummary>();
        var endpoint = $"/api/admin/employees/{employee!.Id}/activation";

        using var missingValue = new StringContent("{}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsync(endpoint, missingValue)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<EmployeeSummary>($"/api/admin/employees/{employee.Id}"))!.IsActive);

        var deactivateResponse = await client.PatchAsJsonAsync(endpoint, new ActivationRequest(false));
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);
        var deactivated = await deactivateResponse.Content.ReadFromJsonAsync<EmployeeSummary>();
        Assert.False(deactivated!.IsActive);

        var reactivateResponse = await client.PatchAsJsonAsync(endpoint, new ActivationRequest(true));
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);
        var reactivated = await reactivateResponse.Content.ReadFromJsonAsync<EmployeeSummary>();
        Assert.True(reactivated!.IsActive);
    }

    [Fact]
    public async Task Malformed_activation_json_returns_bad_request()
    {
        using var client = await AuthenticatedClient("admin@example.com");
        using var malformed = new StringContent("{", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsync("/api/admin/employees/1/activation", malformed)).StatusCode);
    }

    [Fact]
    public async Task Duplicate_and_invalid_employee_requests_are_rejected()
    {
        using var client = await AuthenticatedClient("admin@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/employees", NewEmployee("ADMIN@EXAMPLE.COM", UserRole.Cashier))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/employees", NewEmployee("bad", UserRole.Cashier) with { Password = "weak", ConfirmPassword = "different" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/employees?pageNumber=0&pageSize=101")).StatusCode);
    }

    [Theory]
    [InlineData("invalid-role")]
    [InlineData("weak-password")]
    [InlineData("invalid-email")]
    public async Task Employee_validation_rules_are_independently_enforced(string scenario)
    {
        using var client = await AuthenticatedClient("admin@example.com");
        var request = NewEmployee($"{scenario}@example.com", UserRole.Cashier);
        request = scenario switch
        {
            "invalid-role" => request with { Role = (UserRole)999 },
            "weak-password" => request with { Password = "weak", ConfirmPassword = "weak" },
            "invalid-email" => request with { Email = "not-an-email" },
            _ => request
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/employees", request)).StatusCode);
    }

    [Fact]
    public async Task Created_employee_persists_normalized_email_and_password_hash()
    {
        const string password = "Valid1!Password";
        var email = $"  Persist-{Guid.NewGuid():N}@Example.com  ";
        using var client = await AuthenticatedClient("admin@example.com");
        var response = await client.PostAsJsonAsync("/api/admin/employees", NewEmployee(email, UserRole.Cashier) with { Password = password, ConfirmPassword = password });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EmployeeSummary>();

        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(x => x.Id == created!.Id);
        Assert.Equal(email.Trim().ToUpperInvariant(), user.NormalizedEmail);
        Assert.NotEqual(password, user.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().VerifyHashedPassword(user, user.PasswordHash, password));
    }

    [Fact]
    public async Task Employee_list_honors_page_size_and_descending_id_order()
    {
        using var client = await AuthenticatedClient("admin@example.com");
        for (var index = 0; index < 3; index++)
            (await client.PostAsJsonAsync("/api/admin/employees", NewEmployee($"page-{Guid.NewGuid():N}@example.com", UserRole.Cashier))).EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<PagedResponse<EmployeeSummary>>("/api/admin/employees?pageNumber=1&pageSize=2");
        Assert.Equal(2, page!.Items.Count);
        Assert.True(page.Items[0].Id > page.Items[1].Id);
    }

    [Fact]
    public async Task Password_confirmation_mismatch_does_not_expose_plaintext_values()
    {
        const string password = "Valid1!Password";
        const string confirmPassword = "Different1!Password";
        using var client = await AuthenticatedClient("admin@example.com");
        var request = NewEmployee("password-mismatch@example.com", UserRole.Cashier) with
        {
            Password = password,
            ConfirmPassword = confirmPassword
        };

        var response = await client.PostAsJsonAsync("/api/admin/employees", request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Passwords do not match.", body);
        Assert.DoesNotContain(password, body);
        Assert.DoesNotContain(confirmPassword, body);
        Assert.DoesNotContain("PasswordHash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_employee_is_not_found()
    {
        using var client = await AuthenticatedClient("admin@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/employees/999999")).StatusCode);
    }

    [Fact]
    public async Task Invalid_signature_is_rejected()
    {
        var login = await (await Login("admin@example.com")).Content.ReadFromJsonAsync<LoginResponse>();
        var tokenParts = login!.AccessToken.Split('.');
        Assert.Equal(3, tokenParts.Length);
        var signatureBytes = Base64UrlEncoder.DecodeBytes(tokenParts[2]);
        signatureBytes[0] ^= 0x01;
        var invalidSignatureToken = string.Join('.', tokenParts[0], tokenParts[1], Base64UrlEncoder.Encode(signatureBytes));

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidSignatureToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/employees")).StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var expired = new JwtSecurityToken("RetailPOSApi.Tests", "RetailPOSApi.Tests.Client",
            [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "Admin")],
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(RetailApiFactory.SigningKey)), SecurityAlgorithms.HmacSha256));
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(expired));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/employees")).StatusCode);
    }

    [Theory]
    [InlineData("Wrong.Issuer", "RetailPOSApi.Tests.Client")]
    [InlineData("RetailPOSApi.Tests", "Wrong.Audience")]
    public async Task Token_with_wrong_issuer_or_audience_is_rejected(string issuer, string audience)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateSignedToken(issuer, audience));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/employees")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_describes_bearer_only_for_protected_operations()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out var bearer));
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.True(root.GetProperty("paths").GetProperty("/api/admin/employees").GetProperty("get").GetProperty("security").GetArrayLength() > 0);
        Assert.False(root.GetProperty("paths").GetProperty("/api/auth/login").GetProperty("post").TryGetProperty("security", out _));
        Assert.False(root.GetProperty("paths").GetProperty("/health").GetProperty("get").TryGetProperty("security", out _));
    }

    private Task<HttpResponseMessage> Login(string email, string password = "Valid1!Password") => _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
    private async Task<HttpClient> AuthenticatedClient(string email)
    {
        var login = await Login(email); var json = await login.Content.ReadFromJsonAsync<LoginResponse>();
        var client = _factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json!.AccessToken); return client;
    }
    private static CreateEmployeeRequest NewEmployee(string email, UserRole role) => new("New", "Employee", email, "Valid1!Password", "Valid1!Password", role);
    private static string CreateSignedToken(string issuer, string audience)
    {
        var token = new JwtSecurityToken(issuer, audience,
            [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "Admin")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(RetailApiFactory.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
