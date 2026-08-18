using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Employees;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public enum CreateEmployeeStatus { Created, Duplicate }
public sealed record CreateEmployeeResult(CreateEmployeeStatus Status, EmployeeSummary? Employee);

public interface IEmployeeService
{
    Task<CreateEmployeeResult> CreateAsync(CreateEmployeeRequest request, CancellationToken cancellationToken);
    Task<PagedResponse<EmployeeSummary>> ListAsync(EmployeeQuery query, CancellationToken cancellationToken);
    Task<EmployeeSummary?> GetAsync(int id, CancellationToken cancellationToken);
    Task<EmployeeSummary?> SetActivationAsync(int id, bool isActive, CancellationToken cancellationToken);
}

public sealed class EmployeeService(AppDbContext db, IPasswordHasher<User> hasher, TimeProvider timeProvider) : IEmployeeService
{
    public async Task<CreateEmployeeResult> CreateAsync(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Trim(request.Email);
        var normalized = EmailNormalizer.Normalize(email);
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, cancellationToken)) return new(CreateEmployeeStatus.Duplicate, null);
        var now = timeProvider.GetUtcNow();
        var user = new User { FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(), Email = email, NormalizedEmail = normalized, PasswordHash = "", Role = request.Role, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.Entry(user).State = EntityState.Detached;
            if (await db.Users.AsNoTracking().AnyAsync(x => x.NormalizedEmail == normalized, cancellationToken))
                return new(CreateEmployeeStatus.Duplicate, null);
            throw;
        }
        return new(CreateEmployeeStatus.Created, ToSummary(user));
    }

    public async Task<PagedResponse<EmployeeSummary>> ListAsync(EmployeeQuery query, CancellationToken cancellationToken)
    {
        var users = db.Users.AsNoTracking().AsQueryable();
        if (query.Role.HasValue) users = users.Where(x => x.Role == query.Role.Value);
        if (query.IsActive.HasValue) users = users.Where(x => x.IsActive == query.IsActive.Value);
        var total = await users.CountAsync(cancellationToken);
        var items = await users.OrderByDescending(x => x.Id)
            .Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new EmployeeSummary(x.Id, x.FirstName, x.LastName, x.Email, x.Role, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
        return new(items, query.PageNumber, query.PageSize, total);
    }

    public async Task<EmployeeSummary?> GetAsync(int id, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new EmployeeSummary(x.Id, x.FirstName, x.LastName, x.Email, x.Role, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc)).SingleOrDefaultAsync(cancellationToken);

    public async Task<EmployeeSummary?> SetActivationAsync(int id, bool isActive, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null) return null;
        if (user.IsActive != isActive)
        {
            user.IsActive = isActive;
            user.UpdatedAtUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        return ToSummary(user);
    }

    internal static EmployeeSummary ToSummary(User user) => new(user.Id, user.FirstName, user.LastName, user.Email, user.Role, user.IsActive, user.CreatedAtUtc, user.UpdatedAtUtc);
}
