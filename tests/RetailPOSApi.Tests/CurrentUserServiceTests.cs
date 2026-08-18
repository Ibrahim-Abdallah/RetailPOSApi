using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RetailPOSApi.Domain;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed class CurrentUserServiceTests
{
    [Fact]
    public void Valid_claims_are_exposed()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Email, "user@example.com"), new Claim(ClaimTypes.Role, "Manager")
        }, "test")) };
        var service = new CurrentUserService(new HttpContextAccessor { HttpContext = context });
        Assert.True(service.IsAuthenticated); Assert.Equal(42, service.UserId); Assert.Equal("user@example.com", service.Email); Assert.Equal(UserRole.Manager, service.Role);
    }

    [Fact]
    public void Malformed_or_missing_claims_do_not_become_defaults()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "invalid"), new Claim(ClaimTypes.Role, "Owner") }, "test")) };
        var service = new CurrentUserService(new HttpContextAccessor { HttpContext = context });
        Assert.True(service.IsAuthenticated); Assert.Null(service.UserId); Assert.Null(service.Email); Assert.Null(service.Role);
    }
}
