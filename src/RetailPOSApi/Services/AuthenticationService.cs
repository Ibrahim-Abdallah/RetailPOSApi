using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public interface IAuthenticationService { Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken); }

public sealed class AuthenticationService(AppDbContext db, IPasswordHasher<User> hasher, IJwtTokenService tokens) : IAuthenticationService
{
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var normalizedEmail = EmailNormalizer.Normalize(request.Email);
        var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (user is null || !user.IsActive) return null;

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed) return null;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
            await db.SaveChangesAsync(cancellationToken);
        }

        var token = tokens.Create(user);
        return new(token.Token, token.ExpiresAtUtc, EmployeeService.ToSummary(user));
    }
}
