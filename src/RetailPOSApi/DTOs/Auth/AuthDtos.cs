using RetailPOSApi.Domain;

namespace RetailPOSApi.DTOs.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record EmployeeSummary(int Id, string FirstName, string LastName, string Email, UserRole Role, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record LoginResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, EmployeeSummary Employee);
