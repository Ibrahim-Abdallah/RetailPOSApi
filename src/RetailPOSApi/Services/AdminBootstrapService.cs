using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetailPOSApi.Configuration;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Employees;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public sealed class AdminBootstrapService(AppDbContext db, IOptions<BootstrapAdminOptions> options, IValidator<CreateEmployeeRequest> validator, IPasswordHasher<User> hasher, TimeProvider timeProvider)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        if (!value.Enabled) return;
        var request = new CreateEmployeeRequest(value.FirstName, value.LastName, value.Email, value.Password, value.Password, UserRole.Admin);
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid) throw new InvalidOperationException("Enabled BootstrapAdmin configuration is invalid.");
        var normalized = EmailNormalizer.Normalize(value.Email);
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, cancellationToken)) return;
        var now = timeProvider.GetUtcNow();
        var user = new User { FirstName = value.FirstName.Trim(), LastName = value.LastName.Trim(), Email = EmailNormalizer.Trim(value.Email), NormalizedEmail = normalized, PasswordHash = "", Role = UserRole.Admin, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
        user.PasswordHash = hasher.HashPassword(user, value.Password);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.Entry(user).State = EntityState.Detached;
            if (!await db.Users.AsNoTracking().AnyAsync(x => x.NormalizedEmail == normalized, cancellationToken)) throw;
        }
    }
}
