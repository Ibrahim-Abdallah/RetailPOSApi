using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetailPOSApi.Configuration;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public interface IAuthenticationService
{
    Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<TokenPairResponse?> RefreshAsync(string rawToken, CancellationToken cancellationToken);
    Task LogoutAsync(string rawToken, CancellationToken cancellationToken);
}

public sealed class AuthenticationService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    IJwtTokenService accessTokens,
    IRefreshTokenService refreshTokens,
    IRefreshAttemptCoordinator refreshAttempts,
    IOptions<JwtOptions> options,
    TimeProvider timeProvider) : IAuthenticationService
{
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var normalizedEmail = EmailNormalizer.Normalize(request.Email);
        var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (user is null || !user.IsActive) return null;

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed) return null;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, request.Password);

        var access = accessTokens.Create(user);
        var refresh = CreateRefreshToken(user.Id);
        db.RefreshTokens.Add(refresh.Entity);
        await db.SaveChangesAsync(cancellationToken);
        return new(access.Token, access.ExpiresAtUtc, refresh.RawToken, refresh.Entity.ExpiresAtUtc, EmployeeService.ToSummary(user));
    }

    public async Task<TokenPairResponse?> RefreshAsync(string rawToken, CancellationToken cancellationToken)
    {
        var tokenHash = refreshTokens.Hash(rawToken);
        using var attempt = await refreshAttempts.BeginAsync(tokenHash, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var existing = await db.RefreshTokens.AsNoTracking().Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (existing is null) return null;

        if (existing.RevokedAtUtc is not null && existing.ReplacedByTokenHash is not null)
        {
            if (!attempt.OverlappedAnotherAttempt)
                await RevokeDescendantsAsync(existing.ReplacedByTokenHash, now, cancellationToken);
            return null;
        }
        if (existing.ExpiresAtUtc <= now || !existing.User.IsActive || existing.RevokedAtUtc is not null) return null;

        var replacement = CreateRefreshToken(existing.UserId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.RefreshTokens
            .Where(x => x.Id == existing.Id && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.RevokedAtUtc, now)
                .SetProperty(x => x.ReplacedByTokenHash, replacement.Entity.TokenHash), cancellationToken);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        db.RefreshTokens.Add(replacement.Entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var access = accessTokens.Create(existing.User);
        return new(access.Token, access.ExpiresAtUtc, replacement.RawToken, replacement.Entity.ExpiresAtUtc);
    }

    public async Task LogoutAsync(string rawToken, CancellationToken cancellationToken)
    {
        var hash = refreshTokens.Hash(rawToken);
        var now = timeProvider.GetUtcNow();
        await db.RefreshTokens.Where(x => x.TokenHash == hash && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAtUtc, now), cancellationToken);
    }

    private (string RawToken, RefreshToken Entity) CreateRefreshToken(int userId)
    {
        var raw = refreshTokens.Generate();
        var now = timeProvider.GetUtcNow();
        return (raw, new RefreshToken
        {
            UserId = userId,
            TokenHash = refreshTokens.Hash(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(options.Value.RefreshTokenExpirationDays)
        });
    }

    private async Task RevokeDescendantsAsync(string firstHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { firstHash };
        var currentHash = firstHash;
        for (var depth = 0; depth < 100; depth++)
        {
            var current = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == currentHash, cancellationToken);
            if (current is null) return;
            if (current.RevokedAtUtc is null)
            {
                var revoked = await db.RefreshTokens.Where(x => x.Id == current.Id && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAtUtc, now), cancellationToken);
                if (revoked == 0)
                {
                    current = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(x => x.Id == current.Id, cancellationToken);
                    if (current is null) return;
                }
            }
            if (current.ReplacedByTokenHash is null || !visited.Add(current.ReplacedByTokenHash)) return;
            currentHash = current.ReplacedByTokenHash;
        }
    }
}
