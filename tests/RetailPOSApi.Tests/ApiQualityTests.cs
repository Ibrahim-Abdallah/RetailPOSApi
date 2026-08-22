using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Reports;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed class ApiQualityTests(RetailApiFactory factory) : IClassFixture<RetailApiFactory>
{
    [Fact]
    public async Task Unexpected_exception_returns_safe_problem_details()
    {
        using var throwingFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IReportingService>();
            services.AddScoped<IReportingService, ThrowingReportingService>();
        }));
        using var client = throwingFactory.CreateClient();
        await Authenticate(client, "manager@example.com");

        var response = await client.GetAsync("/api/management/reports/sales-summary");
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(500, problem!.Status);
        Assert.Equal("An unexpected error occurred.", problem.Title);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));
        Assert.DoesNotContain(ThrowingReportingService.SensitiveMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowingReportingService", body, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_accept_header_preserves_unauthorized_status()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync("/api/management/reports/sales-summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_accept_header_uses_safe_unexpected_exception_fallback()
    {
        using var throwingFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IReportingService>();
            services.AddScoped<IReportingService, ThrowingReportingService>();
        }));
        using var client = throwingFactory.CreateClient();
        await Authenticate(client, "manager@example.com");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync("/api/management/reports/sales-summary");
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(500, problem!.Status);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));
        Assert.DoesNotContain(ThrowingReportingService.SensitiveMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowingReportingService", body, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_request_returns_validation_problem_details()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "bad", password = "" });
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(json.RootElement.GetProperty("errors").EnumerateObject().Any());
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_credentials_return_explicit_problem_details()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", "wrong-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(401, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
    }

    [Fact]
    public async Task Business_not_found_remains_problem_details_404()
    {
        using var client = factory.CreateClient();
        await Authenticate(client, "admin@example.com");
        var response = await client.GetAsync("/api/admin/employees/2147483647");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/route-that-does-not-exist", HttpStatusCode.NotFound)]
    [InlineData("/api/management/reports/sales-summary", HttpStatusCode.Unauthorized)]
    public async Task Empty_status_codes_return_problem_details(string path, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expected, problem!.Status);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task Insufficient_role_returns_forbidden_problem_details()
    {
        using var client = factory.CreateClient();
        await Authenticate(client, "cashier@example.com");
        var response = await client.GetAsync("/api/management/reports/sales-summary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(403, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
    }

    [Fact]
    public async Task OpenApi_has_professional_metadata_and_expected_routes()
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var info = root.GetProperty("info");
        Assert.Equal("Retail POS API", info.GetProperty("title").GetString());
        Assert.Equal("v1", info.GetProperty("version").GetString());
        Assert.Contains("transactional retail POS", info.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(root.GetProperty("paths").TryGetProperty("/health", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty("/api/management/reports/sales-summary", out _));
    }

    [Fact]
    public async Task OpenApi_bearer_scheme_and_operation_security_are_accurate()
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var bearer = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.True(root.GetProperty("paths").GetProperty("/api/management/reports/sales-summary").GetProperty("get").GetProperty("security").GetArrayLength() > 0);
        Assert.False(root.GetProperty("paths").GetProperty("/api/auth/login").GetProperty("post").TryGetProperty("security", out _));
        Assert.False(root.GetProperty("paths").GetProperty("/health").GetProperty("get").TryGetProperty("security", out _));
    }

    [Theory]
    [InlineData("/api/cashier/shifts/open", "post", "201", "400", "401", "403", "404", "409")]
    [InlineData("/api/management/reports/sales-summary", "get", "200", "400", "401", "403", null, null)]
    public async Task OpenApi_advertises_representative_responses(string path, string method, params string?[] statuses)
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var responses = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses");
        foreach (var status in statuses.Where(x => x is not null)) Assert.True(responses.TryGetProperty(status!, out _), $"Missing {status} for {method.ToUpperInvariant()} {path}");
    }

    [Fact]
    public async Task Scalar_reference_is_reachable_in_development()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var response = await client.GetAsync("/scalar/v1");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Retail POS API Reference", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Authenticate(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Valid1!Password"));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
    }

    private sealed class ThrowingReportingService : IReportingService
    {
        public const string SensitiveMessage = "sensitive SQL password token C:\\source\\secret.cs";
        public Task<SalesSummaryResponse> GetSalesSummary(ReportQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException(SensitiveMessage);
        public Task<ShiftSummaryResponse> GetShiftSummary(ReportQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException(SensitiveMessage);
    }
}
