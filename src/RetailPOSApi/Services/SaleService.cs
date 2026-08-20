using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public enum SaleOperationStatus { Success, BadRequest, NotFound, Conflict, Forbidden }
public sealed record SaleOperationResult(SaleOperationStatus Status, SaleResponse? Value = null, string? Message = null);

public interface ISaleService
{
    Task<SaleOperationResult> Create(CancellationToken ct);
    Task<SaleOperationResult> AddLine(int saleId, AddSaleLineRequest request, CancellationToken ct);
    Task<SaleOperationResult> UpdateQuantity(int saleId, int lineId, UpdateSaleLineQuantityRequest request, CancellationToken ct);
    Task<SaleOperationResult> ApplyDiscount(int saleId, int lineId, ApplySaleLineDiscountRequest request, CancellationToken ct);
    Task<SaleOperationResult> RemoveDiscount(int saleId, int lineId, CancellationToken ct);
    Task<SaleOperationResult> RemoveLine(int saleId, int lineId, CancellationToken ct);
    Task<PagedResponse<SaleResponse>> ListOwn(SaleQuery query, CancellationToken ct);
    Task<SaleResponse?> GetOwn(int id, CancellationToken ct);
    Task<PagedResponse<SaleResponse>> ListManagement(ManagementSaleQuery query, CancellationToken ct);
    Task<SaleResponse?> GetManagement(int id, CancellationToken ct);
}

public sealed class SaleService(AppDbContext db, ICurrentUserService currentUser, TimeProvider clock) : ISaleService
{
    public async Task<SaleOperationResult> Create(CancellationToken ct)
    {
        var state = await CurrentCashier(ct);
        if (state.Status != SaleOperationStatus.Success) return new(state.Status, null, state.Message);
        var shift = await db.CashierShifts.AsNoTracking().SingleOrDefaultAsync(
            x => x.CashierUserId == state.UserId && x.Status == CashierShiftStatus.Open, ct);
        if (shift is null) return Conflict("An open cashier shift is required to create a sale.");
        var now = clock.GetUtcNow();
        var sale = new Sale
        {
            BranchId = shift.BranchId, RegisterId = shift.RegisterId, CashierShiftId = shift.Id,
            CashierUserId = state.UserId, Status = SaleStatus.Open,
            Subtotal = 0, DiscountTotal = 0, TaxTotal = 0, TotalAmount = 0,
            ReceiptNumber = null, CompletedAtUtc = null, VoidedAtUtc = null,
            VoidedByUserId = null, VoidReason = null, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.Sales.Add(sale);
        await db.SaveChangesAsync(ct);
        return new(SaleOperationStatus.Success, await Detail(db.Sales.AsNoTracking().Where(x => x.Id == sale.Id), ct));
    }

    public Task<SaleOperationResult> AddLine(int saleId, AddSaleLineRequest request, CancellationToken ct) =>
        Mutate(saleId, async sale =>
        {
            var product = await db.Products.Include(x => x.TaxRate).SingleOrDefaultAsync(x => x.Id == request.ProductId, ct);
            if (product is null) return NotFound("Product not found.");
            if (!product.IsActive) return Conflict("Product is inactive.");
            if (!product.TaxRate.IsActive) return Conflict("Tax rate is inactive.");
            Discount? discount = null;
            if (request.DiscountId is int discountId)
            {
                discount = await db.Discounts.SingleOrDefaultAsync(x => x.Id == discountId, ct);
                if (discount is null) return NotFound("Discount not found.");
                if (!discount.IsActive) return Conflict("Discount is inactive.");
            }
            var line = sale.Lines.SingleOrDefault(x => x.ProductId == product.Id && x.DiscountId == request.DiscountId);
            if (line is null)
            {
                line = new SaleLine
                {
                    ProductId = product.Id, ProductSku = product.Sku, ProductName = product.Name,
                    Quantity = request.Quantity, UnitPrice = product.UnitPrice,
                    TaxRateId = product.TaxRateId, TaxRateName = product.TaxRate.Name,
                    TaxRatePercentage = product.TaxRate.Percentage
                };
                SnapshotDiscount(line, discount);
                sale.Lines.Add(line);
            }
            else
            {
                var combinedQuantity = (long)line.Quantity + request.Quantity;
                if (combinedQuantity > int.MaxValue) return BadRequest("Quantity exceeds the supported range.");
                line.Quantity = (int)combinedQuantity;
            }
            if (!SaleCalculation.TryRecalculateLine(line)) return MoneyCapacityFailure();
            return null;
        }, ct);

    public Task<SaleOperationResult> UpdateQuantity(int saleId, int lineId, UpdateSaleLineQuantityRequest request, CancellationToken ct) =>
        MutateLine(saleId, lineId, line =>
        {
            line.Quantity = request.Quantity;
            return Task.FromResult<SaleOperationResult?>(SaleCalculation.TryRecalculateLine(line) ? null : MoneyCapacityFailure());
        }, ct);

    public Task<SaleOperationResult> ApplyDiscount(int saleId, int lineId, ApplySaleLineDiscountRequest request, CancellationToken ct) =>
        MutateLine(saleId, lineId, async line =>
        {
            var discount = await db.Discounts.SingleOrDefaultAsync(x => x.Id == request.DiscountId, ct);
            if (discount is null) return NotFound("Discount not found.");
            if (!discount.IsActive) return Conflict("Discount is inactive.");
            if (line.Sale.Lines.Any(x => x.Id != line.Id && x.ProductId == line.ProductId && x.DiscountId == discount.Id))
                return Conflict("A sale line with this product and discount selection already exists.");
            SnapshotDiscount(line, discount);
            return SaleCalculation.TryRecalculateLine(line) ? null : MoneyCapacityFailure();
        }, ct);

    public Task<SaleOperationResult> RemoveDiscount(int saleId, int lineId, CancellationToken ct) =>
        MutateLine(saleId, lineId, line =>
        {
            if (line.Sale.Lines.Any(x => x.Id != line.Id && x.ProductId == line.ProductId && x.DiscountId == null))
                return Task.FromResult<SaleOperationResult?>(Conflict("A sale line with this product and discount selection already exists."));
            SnapshotDiscount(line, null);
            return Task.FromResult<SaleOperationResult?>(SaleCalculation.TryRecalculateLine(line) ? null : MoneyCapacityFailure());
        }, ct);

    public Task<SaleOperationResult> RemoveLine(int saleId, int lineId, CancellationToken ct) =>
        Mutate(saleId, sale =>
        {
            var line = sale.Lines.SingleOrDefault(x => x.Id == lineId);
            if (line is null) return Task.FromResult<SaleOperationResult?>(NotFound("Sale line not found."));
            db.SaleLines.Remove(line);
            sale.Lines.Remove(line);
            return Task.FromResult<SaleOperationResult?>(null);
        }, ct);

    async Task<SaleOperationResult> MutateLine(int saleId, int lineId, Func<SaleLine, Task<SaleOperationResult?>> action, CancellationToken ct) =>
        await Mutate(saleId, async sale =>
        {
            var line = sale.Lines.SingleOrDefault(x => x.Id == lineId);
            return line is null ? NotFound("Sale line not found.") : await action(line);
        }, ct);

    async Task<SaleOperationResult> Mutate(int saleId, Func<Sale, Task<SaleOperationResult?>> action, CancellationToken ct)
    {
        var state = await CurrentCashier(ct);
        if (state.Status != SaleOperationStatus.Success) return new(state.Status, null, state.Message);
        // SQL Server keeps an explicit transaction around the read/mutate/write cycle.
        // SQLite SaveChanges is itself atomic; avoiding a long-lived SQLite read
        // transaction also permits deterministic relational concurrency testing.
        await using var transaction = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? null
            : await db.Database.BeginTransactionAsync(ct);
        var sale = await db.Sales.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == saleId && x.CashierUserId == state.UserId, ct);
        if (sale is null) return NotFound("Sale not found.");
        if (sale.Status != SaleStatus.Open) return Conflict("Sale is not open.");
        var shift = await db.CashierShifts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sale.CashierShiftId && x.CashierUserId == state.UserId, ct);
        if (shift is null) return Conflict("The sale's cashier shift is not open.");
        var currentShiftId = await db.CashierShifts.AsNoTracking().Where(x => x.CashierUserId == state.UserId && x.Status == CashierShiftStatus.Open).Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);
        if (shift.Status != CashierShiftStatus.Open)
            return currentShiftId.HasValue && currentShiftId != sale.CashierShiftId
                ? Conflict("The sale does not belong to the current open cashier shift.")
                : Conflict("The sale's cashier shift is not open.");
        if (currentShiftId != sale.CashierShiftId) return Conflict("The sale does not belong to the current open cashier shift.");
        var failure = await action(sale);
        if (failure is not null) return await RejectMutation(transaction, failure, ct);
        if (!SaleCalculation.TryRecalculateSale(sale)) return await RejectMutation(transaction, MoneyCapacityFailure(), ct);
        sale.UpdatedAtUtc = clock.GetUtcNow();
        // Force a Sale UPDATE even under a fixed test clock so RowVersion guards
        // every line mutation, including mutations whose resulting totals match.
        db.Entry(sale).Property(x => x.UpdatedAtUtc).IsModified = true;
        try
        {
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return Conflict("The sale was modified by another request. Retry with the latest sale state.");
        }
        return new(SaleOperationStatus.Success, await GetOwn(saleId, ct));
    }

    public async Task<PagedResponse<SaleResponse>> ListOwn(SaleQuery query, CancellationToken ct)
    {
        var id = currentUser.UserId ?? 0;
        return await Page(db.Sales.AsNoTracking().Where(x => x.CashierUserId == id), query, ct);
    }

    public Task<SaleResponse?> GetOwn(int id, CancellationToken ct) =>
        currentUser.UserId is int userId ? Detail(db.Sales.AsNoTracking().Where(x => x.Id == id && x.CashierUserId == userId), ct) : Task.FromResult<SaleResponse?>(null);

    public Task<PagedResponse<SaleResponse>> ListManagement(ManagementSaleQuery query, CancellationToken ct)
    {
        var source = db.Sales.AsNoTracking().AsQueryable();
        if (query.BranchId.HasValue) source = source.Where(x => x.BranchId == query.BranchId);
        if (query.RegisterId.HasValue) source = source.Where(x => x.RegisterId == query.RegisterId);
        if (query.CashierUserId.HasValue) source = source.Where(x => x.CashierUserId == query.CashierUserId);
        return Page(source, query, ct);
    }

    public Task<SaleResponse?> GetManagement(int id, CancellationToken ct) => Detail(db.Sales.AsNoTracking().Where(x => x.Id == id), ct);

    async Task<PagedResponse<SaleResponse>> Page(IQueryable<Sale> source, SaleQuery query, CancellationToken ct)
    {
        if (query.Status.HasValue) source = source.Where(x => x.Status == query.Status);
        if (query.CashierShiftId.HasValue) source = source.Where(x => x.CashierShiftId == query.CashierShiftId);
        var total = await source.CountAsync(ct);
        source = (query.SortBy.ToLowerInvariant(), query.SortDirection.ToLowerInvariant()) switch
        {
            ("totalamount", "asc") => source.OrderBy(x => x.TotalAmount).ThenBy(x => x.Id),
            ("totalamount", _) => source.OrderByDescending(x => x.TotalAmount).ThenBy(x => x.Id),
            ("createdat", "asc") => source.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            _ => source.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
        };
        var values = await SaleProjection.Project(source.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)).ToListAsync(ct);
        return new(values, query.Page, query.PageSize, total, (int)Math.Ceiling(total / (double)query.PageSize));
    }

    static Task<SaleResponse?> Detail(IQueryable<Sale> source, CancellationToken ct) => SaleProjection.Project(source).SingleOrDefaultAsync(ct);

    async Task<(SaleOperationStatus Status, int UserId, string? Message)> CurrentCashier(CancellationToken ct)
    {
        if (currentUser.UserId is not int id) return (SaleOperationStatus.Forbidden, 0, "Authenticated cashier identity is unavailable.");
        var valid = await db.Users.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive && x.Role == UserRole.Cashier, ct);
        return valid ? (SaleOperationStatus.Success, id, null) : (SaleOperationStatus.Forbidden, id, "Cashier is not active.");
    }

    static void SnapshotDiscount(SaleLine line, Discount? discount)
    {
        line.DiscountId = discount?.Id;
        line.DiscountName = discount?.Name;
        line.DiscountType = discount?.Type;
        line.DiscountValue = discount?.Value;
    }
    static SaleOperationResult NotFound(string message) => new(SaleOperationStatus.NotFound, null, message);
    static SaleOperationResult BadRequest(string message) => new(SaleOperationStatus.BadRequest, null, message);
    static SaleOperationResult Conflict(string message) => new(SaleOperationStatus.Conflict, null, message);
    static SaleOperationResult MoneyCapacityFailure() => BadRequest("The requested calculation exceeds the supported monetary precision.");

    async Task<SaleOperationResult> RejectMutation(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, SaleOperationResult result, CancellationToken ct)
    {
        if (transaction is not null) await transaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return result;
    }
}

public static class SaleProjection
{
    public static IQueryable<SaleResponse> Project(IQueryable<Sale> source) => source.Select(x => new SaleResponse(
        x.Id, x.Status, x.BranchId, x.Branch.Code, x.Branch.Name, x.RegisterId, x.Register.Code, x.Register.Name,
        x.CashierShiftId, x.CashierUserId, x.CashierUser.FirstName + " " + x.CashierUser.LastName,
        x.Subtotal, x.DiscountTotal, x.TaxTotal, x.TotalAmount, x.ReceiptNumber, x.CompletedAtUtc,
        x.VoidedAtUtc, x.VoidedByUserId,
        x.VoidedByUser == null ? null : x.VoidedByUser.FirstName + " " + x.VoidedByUser.LastName, x.VoidReason,
        x.CreatedAtUtc, x.UpdatedAtUtc,
        x.Lines.OrderBy(l => l.Id).Select(l => new SaleLineResponse(
            l.Id, l.ProductId, l.ProductSku, l.ProductName, l.Quantity, l.UnitPrice,
            l.DiscountId, l.DiscountName, l.DiscountType, l.DiscountValue, l.UnitDiscountAmount, l.UnitNetAmount,
            l.TaxRateId, l.TaxRateName, l.TaxRatePercentage, l.UnitTaxAmount, l.UnitTotal,
            l.LineSubtotal, l.LineDiscountTotal, l.LineTaxTotal, l.LineTotal)).ToList(),
        x.Payments.OrderBy(p => p.Id).Select(p => new PaymentResponse(
            p.Id, p.Method, p.AmountApplied, p.TenderedAmount, p.ChangeAmount,
            p.ExternalReference, p.Status, p.CreatedAtUtc)).ToList()));
}
