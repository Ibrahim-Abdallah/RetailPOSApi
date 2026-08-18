using System.Security.Claims;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Services;

public interface ICurrentUserService
{
    bool IsAuthenticated { get; }
    int? UserId { get; }
    string? Email { get; }
    UserRole? Role { get; }
}

public sealed class CurrentUserService(IHttpContextAccessor accessor) : ICurrentUserService
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public int? UserId => int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : null;
    public string? Email => IsAuthenticated ? Principal?.FindFirstValue(ClaimTypes.Email) : null;
    public UserRole? Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(ClaimTypes.Role), out var role) && Enum.IsDefined(role) ? role : null;
}
