using Microsoft.AspNetCore.Mvc.Testing;

namespace RetailPOSApi.Tests;

public sealed class HealthEndpointTests : IClassFixture<RetailApiFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(RetailApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_returns_success()
    {
        using var response = await _client.GetAsync("/health");

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("Healthy", body);
    }
}
