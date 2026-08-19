using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RetailPOSApi.Domain;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;

namespace RetailPOSApi.Tests;

public sealed class RetailApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "tests-only-signing-key-with-at-least-32-characters";
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"retail-pos-tests-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_databasePath}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "RetailPOSApi.Tests", ["Jwt:Audience"] = "RetailPOSApi.Tests.Client",
            ["Jwt:SigningKey"] = SigningKey, ["Jwt:AccessTokenExpirationMinutes"] = "15",
            ["Jwt:RefreshTokenExpirationDays"] = "7",
            ["BootstrapAdmin:Enabled"] = "false"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddSingleton<CashierShiftSaveCoordinator>();
            services.AddSingleton<SaleMutationSaveCoordinator>();
            services.AddDbContext<AppDbContext>((provider, options) => options
                .UseSqlite(ConnectionString)
                .AddInterceptors(provider.GetRequiredService<CashierShiftSaveCoordinator>(), provider.GetRequiredService<SaleMutationSaveCoordinator>()));
        });
    }

    public async Task InitializeAsync()
    {
        _ = CreateClient();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await SeedAsync(scope.ServiceProvider, db, UserRole.Admin, "admin@example.com");
        await SeedAsync(scope.ServiceProvider, db, UserRole.Manager, "manager@example.com");
        await SeedAsync(scope.ServiceProvider, db, UserRole.Cashier, "cashier@example.com");
        await SeedAsync(scope.ServiceProvider, db, UserRole.Cashier, "inactive@example.com", false);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    private static async Task SeedAsync(IServiceProvider provider, AppDbContext db, UserRole role, string email, bool active = true)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User { FirstName = role.ToString(), LastName = "User", Email = email, NormalizedEmail = EmailNormalizer.Normalize(email), PasswordHash = "", Role = role, IsActive = active, CreatedAtUtc = now, UpdatedAtUtc = now };
        user.PasswordHash = provider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, "Valid1!Password");
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }
}
