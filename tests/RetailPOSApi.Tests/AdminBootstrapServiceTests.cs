using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetailPOSApi.Configuration;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Employees;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;
using RetailPOSApi.Validation;

namespace RetailPOSApi.Tests;

public sealed class AdminBootstrapServiceTests
{
    [Fact]
    public async Task Disabled_bootstrap_creates_no_user()
    {
        await using var database = await BootstrapDatabase.CreateAsync();
        var service = database.CreateService(new BootstrapAdminOptions { Enabled = false });

        await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, await database.Context.Users.CountAsync());
    }

    [Fact]
    public async Task Enabled_bootstrap_creates_one_valid_admin_and_is_idempotent()
    {
        await using var database = await BootstrapDatabase.CreateAsync();
        const string bootstrapPassword = "Bootstrap1!TestOnly";
        var service = database.CreateService(new BootstrapAdminOptions
        {
            Enabled = true,
            FirstName = "  Bootstrap ",
            LastName = " Admin  ",
            Email = "  Bootstrap.Admin@Example.com  ",
            Password = bootstrapPassword
        });

        await service.RunAsync(CancellationToken.None);
        await service.RunAsync(CancellationToken.None);

        var user = await database.Context.Users.AsNoTracking().SingleAsync();
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.True(user.IsActive);
        Assert.Equal("Bootstrap.Admin@Example.com", user.Email);
        Assert.Equal("BOOTSTRAP.ADMIN@EXAMPLE.COM", user.NormalizedEmail);
        Assert.StartsWith("AQAAAA", user.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            database.Hasher.VerifyHashedPassword(user, user.PasswordHash, bootstrapPassword));
        Assert.Equal(1, await database.Context.Users.CountAsync());
    }

    private sealed class BootstrapDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public IPasswordHasher<User> Hasher { get; } = new PasswordHasher<User>();

        public static async Task<BootstrapDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new BootstrapDatabase(connection, context);
        }

        public AdminBootstrapService CreateService(BootstrapAdminOptions options) => new(
            Context,
            Options.Create(options),
            new CreateEmployeeRequestValidator(),
            Hasher,
            TimeProvider.System);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
