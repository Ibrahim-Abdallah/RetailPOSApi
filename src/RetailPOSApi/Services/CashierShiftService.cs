using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.DTOs.Shifts;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public enum ShiftOperationStatus { Success, NotFound, Conflict, Forbidden }
public sealed record ShiftOperationResult(ShiftOperationStatus Status, ShiftResponse? Value = null, string? Message = null);

public interface ICashierShiftService
{
    Task<ShiftOperationResult> Open(OpenShiftRequest request, CancellationToken ct);
    Task<ShiftResponse?> Current(CancellationToken ct);
    Task<PagedResponse<ShiftResponse>> ListOwn(ShiftQuery query, CancellationToken ct);
    Task<ShiftResponse?> GetOwn(int id, CancellationToken ct);
    Task<PagedResponse<ShiftResponse>> ListManagement(ManagementShiftQuery query, CancellationToken ct);
    Task<ShiftResponse?> GetManagement(int id, CancellationToken ct);
}

public sealed class CashierShiftService(AppDbContext db, ICurrentUserService currentUser, TimeProvider clock) : ICashierShiftService
{
    public async Task<ShiftOperationResult> Open(OpenShiftRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not int cashierId)
            return new(ShiftOperationStatus.Forbidden, null, "Authenticated cashier identity is unavailable.");

        var cashier = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cashierId, ct);
        if (cashier is null || !cashier.IsActive || cashier.Role != UserRole.Cashier)
            return new(ShiftOperationStatus.Forbidden, null, "Cashier is not active.");

        var register = await db.Registers.Include(x => x.Branch).SingleOrDefaultAsync(x => x.Id == request.RegisterId, ct);
        if (register is null) return new(ShiftOperationStatus.NotFound, null, "Register not found.");
        if (!register.IsActive) return new(ShiftOperationStatus.Conflict, null, "Register is inactive.");
        if (!register.Branch.IsActive) return new(ShiftOperationStatus.Conflict, null, "Branch is inactive.");
        if (await db.CashierShifts.AnyAsync(x => x.CashierUserId == cashierId && x.Status == CashierShiftStatus.Open, ct))
            return new(ShiftOperationStatus.Conflict, null, "Cashier already has an open shift.");
        if (await db.CashierShifts.AnyAsync(x => x.RegisterId == register.Id && x.Status == CashierShiftStatus.Open, ct))
            return new(ShiftOperationStatus.Conflict, null, "Register already has an open shift.");

        var now = clock.GetUtcNow();
        var shift = new CashierShift
        {
            BranchId = register.BranchId,
            RegisterId = register.Id,
            CashierUserId = cashierId,
            Status = CashierShiftStatus.Open,
            OpeningFloat = Math.Round(request.OpeningFloat, 2, MidpointRounding.AwayFromZero),
            OpenedAtUtc = now,
            ClosedAtUtc = null,
            DeclaredCash = null,
            ExpectedCash = null,
            CashVariance = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CashierShifts.Add(shift);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            db.ChangeTracker.Clear();
            if (await db.CashierShifts.AsNoTracking().AnyAsync(x => x.CashierUserId == cashierId && x.Status == CashierShiftStatus.Open, ct))
                return new(ShiftOperationStatus.Conflict, null, "Cashier already has an open shift.");
            if (await db.CashierShifts.AsNoTracking().AnyAsync(x => x.RegisterId == register.Id && x.Status == CashierShiftStatus.Open, ct))
                return new(ShiftOperationStatus.Conflict, null, "Register already has an open shift.");
            throw;
        }

        return new(ShiftOperationStatus.Success, new ShiftResponse(shift.Id, register.BranchId, register.Branch.Code,
            register.Branch.Name, register.Id, register.Code, register.Name, cashier.Id,
            $"{cashier.FirstName} {cashier.LastName}", shift.Status, shift.OpeningFloat, shift.OpenedAtUtc,
            shift.ClosedAtUtc, shift.CreatedAtUtc, shift.UpdatedAtUtc));
    }

    public Task<ShiftResponse?> Current(CancellationToken ct) => BaseQuery()
        .Where(x => x.CashierUserId == currentUser.UserId && x.Status == CashierShiftStatus.Open)
        .Select(Project()).SingleOrDefaultAsync(ct);

    public Task<ShiftResponse?> GetOwn(int id, CancellationToken ct) => BaseQuery()
        .Where(x => x.Id == id && x.CashierUserId == currentUser.UserId).Select(Project()).SingleOrDefaultAsync(ct);

    public Task<ShiftResponse?> GetManagement(int id, CancellationToken ct) => BaseQuery()
        .Where(x => x.Id == id).Select(Project()).SingleOrDefaultAsync(ct);

    public Task<PagedResponse<ShiftResponse>> ListOwn(ShiftQuery query, CancellationToken ct) =>
        List(BaseQuery().Where(x => x.CashierUserId == currentUser.UserId), query, ct);

    public Task<PagedResponse<ShiftResponse>> ListManagement(ManagementShiftQuery query, CancellationToken ct)
    {
        var shifts = BaseQuery();
        if (query.BranchId.HasValue) shifts = shifts.Where(x => x.BranchId == query.BranchId);
        if (query.RegisterId.HasValue) shifts = shifts.Where(x => x.RegisterId == query.RegisterId);
        if (query.CashierUserId.HasValue) shifts = shifts.Where(x => x.CashierUserId == query.CashierUserId);
        return List(shifts, query, ct);
    }

    async Task<PagedResponse<ShiftResponse>> List(IQueryable<CashierShift> shifts, ShiftQuery query, CancellationToken ct)
    {
        if (query.Status.HasValue) shifts = shifts.Where(x => x.Status == query.Status);
        var total = await shifts.CountAsync(ct);
        shifts = (query.SortBy.ToLowerInvariant(), query.SortDirection.ToLowerInvariant()) switch
        {
            ("openedat", "asc") => shifts.OrderBy(x => x.OpenedAtUtc).ThenBy(x => x.Id),
            ("openedat", _) => shifts.OrderByDescending(x => x.OpenedAtUtc).ThenBy(x => x.Id),
            ("createdat", "asc") => shifts.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            ("createdat", _) => shifts.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            ("openingfloat", "asc") => shifts.OrderBy(x => x.OpeningFloat).ThenBy(x => x.Id),
            _ => shifts.OrderByDescending(x => x.OpeningFloat).ThenBy(x => x.Id)
        };
        var items = await shifts.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(Project()).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, total, (int)Math.Ceiling(total / (double)query.PageSize));
    }

    IQueryable<CashierShift> BaseQuery() => db.CashierShifts.AsNoTracking();

    static System.Linq.Expressions.Expression<Func<CashierShift, ShiftResponse>> Project() => x => new ShiftResponse(
        x.Id, x.BranchId, x.Branch.Code, x.Branch.Name, x.RegisterId, x.Register.Code, x.Register.Name,
        x.CashierUserId, x.CashierUser.FirstName + " " + x.CashierUser.LastName, x.Status, x.OpeningFloat,
        x.OpenedAtUtc, x.ClosedAtUtc, x.CreatedAtUtc, x.UpdatedAtUtc);

    static bool IsUniqueViolation(DbUpdateException exception)
    {
        if (exception.GetBaseException() is not DbException databaseException) return false;
        if (databaseException.ErrorCode == 19) return true; // SQLite relational integration tests.
        if (databaseException.GetType().GetProperty("SqliteErrorCode")?.GetValue(databaseException) is 19) return true;
        var number = databaseException.GetType().GetProperty("Number")?.GetValue(databaseException);
        return number is 2601 or 2627; // SQL Server duplicate key / unique index.
    }
}
