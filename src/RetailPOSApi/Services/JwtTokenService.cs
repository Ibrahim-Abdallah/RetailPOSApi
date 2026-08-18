using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RetailPOSApi.Configuration;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Services;

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAtUtc);

public interface IJwtTokenService { AccessTokenResult Create(User user); }

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider) : IJwtTokenService
{
    public AccessTokenResult Create(User user)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(settings.AccessTokenExpirationMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, now.UtcDateTime, expires.UtcDateTime, credentials);
        return new(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
