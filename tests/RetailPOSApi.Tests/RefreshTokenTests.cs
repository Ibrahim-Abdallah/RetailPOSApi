using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed class RefreshTokenTests : IClassFixture<RetailApiFactory>
{
    private readonly RetailApiFactory _factory;
    private readonly HttpClient _client;

    public RefreshTokenTests(RetailApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_issues_distinct_refresh_sessions_and_persists_only_matching_hashes()
    {
        var first = await Login();
        var second = await Login();
        Assert.NotEmpty(first.RefreshToken);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.True(first.RefreshTokenExpiresAtUtc > DateTimeOffset.UtcNow);

        using var scope = _factory.Services.CreateScope();
        var crypto = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == first.Employee.Id).ToListAsync();
        Assert.Contains(rows, x => x.TokenHash == crypto.Hash(first.RefreshToken));
        Assert.Contains(rows, x => x.TokenHash == crypto.Hash(second.RefreshToken));
        Assert.DoesNotContain(rows, x => x.TokenHash == first.RefreshToken || x.TokenHash == second.RefreshToken);
    }

    [Fact]
    public async Task Refresh_rotates_and_persists_one_active_replacement()
    {
        var login = await Login();
        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(login.RefreshToken));
        response.EnsureSuccessStatusCode();
        var rotated = (await response.Content.ReadFromJsonAsync<TokenPairResponse>())!;
        Assert.NotEmpty(rotated.AccessToken);
        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);

        using var scope = _factory.Services.CreateScope();
        var crypto = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var old = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.TokenHash == crypto.Hash(login.RefreshToken));
        var replacement = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.TokenHash == crypto.Hash(rotated.RefreshToken));
        Assert.NotNull(old.RevokedAtUtc);
        Assert.Equal(replacement.TokenHash, old.ReplacedByTokenHash);
        Assert.Null(replacement.RevokedAtUtc);
        Assert.InRange(replacement.ExpiresAtUtc, DateTimeOffset.UtcNow.AddDays(6.9), DateTimeOffset.UtcNow.AddDays(7.1));
    }

    [Fact]
    public async Task Multi_step_rotation_works_and_replay_revokes_active_descendant_generically()
    {
        var a = await Login();
        var b = await Refresh(a.RefreshToken);
        var c = await Refresh(b.RefreshToken);

        var replay = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(a.RefreshToken));
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.DoesNotContain(a.RefreshToken, replayBody);
        Assert.DoesNotContain("replay", replayBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(c.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Replay_and_logout_are_isolated_between_login_sessions()
    {
        var sessionA = await Login();
        var sessionB = await Login();
        var rotatedA = await Refresh(sessionA.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(sessionA.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(rotatedA.RefreshToken))).StatusCode);
        var rotatedB = await Refresh(sessionB.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsJsonAsync("/api/auth/logout", new RefreshTokenRequest(rotatedB.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsJsonAsync("/api/auth/logout", new RefreshTokenRequest(rotatedB.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Logout_is_idempotent_non_enumerating_and_revokes_active_token()
    {
        var login = await Login();
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsJsonAsync("/api/auth/logout", new RefreshTokenRequest(login.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsJsonAsync("/api/auth/logout", new RefreshTokenRequest("unknown-but-syntactically-valid"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(login.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Unknown_refresh_token_is_generic_unauthorized_without_echo()
    {
        const string unknown = "unknown-refresh-credential";
        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(unknown));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(unknown, body);
        Assert.DoesNotContain("unknown", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expired_active_refresh_token_is_generic_unauthorized_without_replacement()
    {
        var login = await Login();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var crypto = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            var token = await db.RefreshTokens.SingleAsync(x => x.TokenHash == crypto.Hash(login.RefreshToken));
            token.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(login.RefreshToken));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("expired", body, StringComparison.OrdinalIgnoreCase);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = verify.ServiceProvider.GetRequiredService<IRefreshTokenService>().Hash(login.RefreshToken);
        Assert.Null((await verifyDb.RefreshTokens.AsNoTracking().SingleAsync(x => x.TokenHash == hash)).ReplacedByTokenHash);
    }

    [Fact]
    public async Task Logout_revokes_only_the_submitted_session()
    {
        var sessionA = await Login();
        var sessionB = await Login();
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsJsonAsync("/api/auth/logout", new RefreshTokenRequest(sessionA.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(sessionA.RefreshToken))).StatusCode);
        Assert.NotNull(await Refresh(sessionB.RefreshToken));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"refreshToken\":null}")]
    [InlineData("{\"refreshToken\":\"   \"}")]
    [InlineData("{")]
    public async Task Refresh_and_logout_reject_invalid_request_shapes(string json)
    {
        foreach (var endpoint in new[] { "/api/auth/refresh", "/api/auth/logout" })
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(endpoint, content)).StatusCode);
        }
    }

    [Fact]
    public async Task Refresh_and_logout_reject_over_limit_token_without_echo()
    {
        var oversized = new string('x', 513);
        foreach (var endpoint in new[] { "/api/auth/refresh", "/api/auth/logout" })
        {
            var response = await _client.PostAsJsonAsync(endpoint, new RefreshTokenRequest(oversized));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(oversized, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Inactive_user_cannot_refresh_and_no_replacement_is_created()
    {
        var login = await Login();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == login.Employee.Id);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(login.RefreshToken))).StatusCode);
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var crypto = verifyScope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var original = await verifyDb.RefreshTokens.SingleAsync(x => x.TokenHash == crypto.Hash(login.RefreshToken));
        Assert.Null(original.ReplacedByTokenHash);
        var restoredUser = await verifyDb.Users.SingleAsync(x => x.Id == login.Employee.Id);
        restoredUser.IsActive = true;
        await verifyDb.SaveChangesAsync();
    }

    [Fact]
    public async Task Expired_rotated_ancestor_replay_revokes_descendant_but_not_independent_session()
    {
        var a = await Login();
        var independent = await Login();
        var b = await Refresh(a.RefreshToken);
        await SetTokenExpiry(a.RefreshToken, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(a.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(b.RefreshToken))).StatusCode);
        Assert.NotNull(await Refresh(independent.RefreshToken));
    }

    [Fact]
    public async Task Inactive_user_rotated_ancestor_replay_revokes_descendant_after_reactivation()
    {
        var a = await Login();
        var b = await Refresh(a.RefreshToken);
        await SetUserActive(a.Employee.Id, false);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(a.RefreshToken))).StatusCode);
        await SetUserActive(a.Employee.Id, true);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(b.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_refresh_winner_remains_usable_then_later_replay_revokes_its_descendant()
    {
        var login = await Login();
        var independent = await Login();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        async Task<HttpResponseMessage> SendAsync()
        {
            if (Interlocked.Increment(ref readyCount) == 2) ready.SetResult();
            await release.Task;
            return await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(login.RefreshToken));
        }
        var requestOne = SendAsync();
        var requestTwo = SendAsync();
        await ready.Task;
        release.SetResult();
        var responses = await Task.WhenAll(requestOne, requestTwo);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        var winner = (await responses.Single(x => x.StatusCode == HttpStatusCode.OK)
            .Content.ReadFromJsonAsync<TokenPairResponse>())!;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var crypto = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var original = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.TokenHash == crypto.Hash(login.RefreshToken));
        Assert.Equal(crypto.Hash(winner.RefreshToken), original.ReplacedByTokenHash);
        var replacements = await db.RefreshTokens.AsNoTracking()
            .Where(x => x.TokenHash == original.ReplacedByTokenHash).ToListAsync();
        Assert.Single(replacements);
        Assert.Null(replacements[0].RevokedAtUtc);

        var descendant = await Refresh(winner.RefreshToken);
        var rotatedWinner = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.TokenHash == crypto.Hash(winner.RefreshToken));
        Assert.NotNull(rotatedWinner.RevokedAtUtc);
        Assert.Equal(crypto.Hash(descendant.RefreshToken), rotatedWinner.ReplacedByTokenHash);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(login.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(descendant.RefreshToken))).StatusCode);
        Assert.NotNull(await Refresh(independent.RefreshToken));
    }

    [Fact]
    public async Task Replay_concurrent_with_descendant_rotation_leaves_no_active_descendant_or_siblings()
    {
        var a = await Login();
        var b = await Refresh(a.RefreshToken);
        var independent = await Login();
        var responses = await SendTogether(
            new RefreshTokenRequest(a.RefreshToken),
            new RefreshTokenRequest(b.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, responses[0].StatusCode);
        Assert.Contains(responses[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var crypto = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var aRow = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.TokenHash == crypto.Hash(a.RefreshToken));
        var chainHashes = new HashSet<string>(StringComparer.Ordinal) { aRow.TokenHash };
        var next = aRow.ReplacedByTokenHash;
        while (next is not null && chainHashes.Add(next))
        {
            var row = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.TokenHash == next);
            Assert.NotNull(row.RevokedAtUtc);
            next = row.ReplacedByTokenHash;
        }
        Assert.NotNull(await Refresh(independent.RefreshToken));
    }

    [Fact]
    public async Task OpenApi_keeps_refresh_and_logout_anonymous()
    {
        using var document = System.Text.Json.JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        Assert.False(paths.GetProperty("/api/auth/refresh").GetProperty("post").TryGetProperty("security", out _));
        Assert.False(paths.GetProperty("/api/auth/logout").GetProperty("post").TryGetProperty("security", out _));
        Assert.True(paths.GetProperty("/api/admin/employees").GetProperty("get").GetProperty("security").GetArrayLength() > 0);
    }

    [Fact]
    public void Invalid_refresh_token_lifetime_fails_startup_validation()
    {
        using var invalidFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:RefreshTokenExpirationDays"] = "0"
                })));

        Assert.ThrowsAny<Exception>(() => invalidFactory.CreateClient());
    }

    private async Task<LoginResponse> Login()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("cashier@example.com", "Valid1!Password"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private async Task<TokenPairResponse> Refresh(string token)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPairResponse>())!;
    }

    private async Task SetTokenExpiry(string rawToken, DateTimeOffset expiry)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().Hash(rawToken);
        var token = await db.RefreshTokens.SingleAsync(x => x.TokenHash == hash);
        token.ExpiresAtUtc = expiry;
        await db.SaveChangesAsync();
    }

    private async Task SetUserActive(int userId, bool active)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(x => x.Id == userId);
        user.IsActive = active;
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage[]> SendTogether(RefreshTokenRequest first, RefreshTokenRequest second)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Send(RefreshTokenRequest request)
        {
            await release.Task;
            return await _client.PostAsJsonAsync("/api/auth/refresh", request);
        }
        var requests = new[] { Send(first), Send(second) };
        release.SetResult();
        return await Task.WhenAll(requests);
    }
}

public sealed class RefreshAttemptCoordinatorTests
{
    [Fact]
    public async Task Overlap_is_sticky_for_all_participants_when_second_finishes_first()
    {
        var coordinator = new RefreshAttemptCoordinator();
        var firstTask = coordinator.BeginAsync("HASH", CancellationToken.None);
        await Task.Delay(5);
        using (var second = await coordinator.BeginAsync("HASH", CancellationToken.None))
            Assert.True(second.OverlappedAnotherAttempt);
        using var first = await firstTask;
        Assert.True(first.OverlappedAnotherAttempt);
    }

    [Fact]
    public async Task Participant_observes_overlap_that_begins_after_its_join_delay_completed()
    {
        var coordinator = new RefreshAttemptCoordinator();
        using var first = await coordinator.BeginAsync("HASH", CancellationToken.None);
        Assert.False(first.OverlappedAnotherAttempt);
        using (var second = await coordinator.BeginAsync("HASH", CancellationToken.None))
        {
            Assert.True(second.OverlappedAnotherAttempt);
            Assert.True(first.OverlappedAnotherAttempt);
        }
        Assert.True(first.OverlappedAnotherAttempt);
    }

    [Fact]
    public async Task Cancellation_cleans_up_state_and_dispose_is_idempotent()
    {
        var coordinator = new RefreshAttemptCoordinator();
        using var cancellation = new CancellationTokenSource();
        var cancelled = coordinator.BeginAsync("HASH", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var isolated = await coordinator.BeginAsync("HASH", CancellationToken.None);
        Assert.False(isolated.OverlappedAnotherAttempt);
        isolated.Dispose();
        isolated.Dispose();
        using var later = await coordinator.BeginAsync("HASH", CancellationToken.None);
        Assert.False(later.OverlappedAnotherAttempt);
    }
}
