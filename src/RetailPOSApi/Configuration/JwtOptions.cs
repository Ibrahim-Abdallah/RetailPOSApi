using System.ComponentModel.DataAnnotations;

namespace RetailPOSApi.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; init; } = "";
    [Required] public string Audience { get; init; } = "";
    [Required, MinLength(32)] public string SigningKey { get; init; } = "";
    [Range(1, 1440)] public int AccessTokenExpirationMinutes { get; init; } = 15;
    [Range(1, 90)] public int RefreshTokenExpirationDays { get; init; } = 7;
}

public sealed class BootstrapAdminOptions
{
    public const string SectionName = "BootstrapAdmin";
    public bool Enabled { get; init; }
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Email { get; init; } = "";
    public string Password { get; init; } = "";
}
