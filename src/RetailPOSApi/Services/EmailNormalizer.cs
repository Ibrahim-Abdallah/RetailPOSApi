namespace RetailPOSApi.Services;

public static class EmailNormalizer
{
    public static string Trim(string email) => email.Trim();
    public static string Normalize(string email) => Trim(email).ToUpperInvariant();
}
