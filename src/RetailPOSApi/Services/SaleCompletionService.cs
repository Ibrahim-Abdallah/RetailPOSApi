using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public interface ISaleCompletionService
{
    Task<SaleOperationResult> Complete(int saleId, CompleteSaleRequest request, CancellationToken ct);
}

public sealed class SaleCompletionService(AppDbContext db, ICurrentUserService currentUser, TimeProvider clock) : ISaleCompletionService
{
    public async Task<SaleOperationResult> Complete(int saleId, CompleteSaleRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not int userId)
            return Forbidden("Authenticated cashier identity is unavailable.");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.IsActive && x.Role == UserRole.Cashier, ct))
            return Forbidden("Cashier is not active.");

        var key = request.IdempotencyKey.Trim();
        var normalized = request.Payments.Select(x => new NormalizedPayment(
            x.Method, x.AmountApplied, x.TenderedAmount, NormalizeReference(x.ExternalReference))).ToList();
        var hash = RequestHash(normalized);

        await using var transaction = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? null
            : await db.Database.BeginTransactionAsync(ct);
        try
        {
            var sale = await db.Sales.Include(x => x.Lines).ThenInclude(x => x.Product)
                .Include(x => x.Payments)
                .SingleOrDefaultAsync(x => x.Id == saleId && x.CashierUserId == userId, ct);
            if (sale is null) return NotFound("Sale not found.");

            if (sale.Status == SaleStatus.Completed)
                return await CompletedResult(sale, key, hash, transaction, ct);
            if (sale.Status != SaleStatus.Open) return Conflict("Sale is not open.");

            var shift = await db.CashierShifts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == sale.CashierShiftId && x.CashierUserId == userId, ct);
            var currentShiftId = await db.CashierShifts.AsNoTracking()
                .Where(x => x.CashierUserId == userId && x.Status == CashierShiftStatus.Open)
                .Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);
            if (shift is null || shift.Status != CashierShiftStatus.Open)
                return Conflict(currentShiftId.HasValue && currentShiftId != sale.CashierShiftId
                    ? "The sale does not belong to the current open cashier shift."
                    : "The sale's cashier shift is not open.");
            if (currentShiftId != sale.CashierShiftId)
                return Conflict("The sale does not belong to the current open cashier shift.");
            if (sale.Lines.Count == 0)
                return Conflict("Sale must contain at least one line before completion.");
            if (sale.Lines.Any(x => !x.Product.IsActive))
                return Conflict("One or more sale products are inactive.");

            foreach (var line in sale.Lines)
                if (!SaleCalculation.TryRecalculateLine(line)) return await Reject(transaction, MoneyFailure(), ct);
            if (!SaleCalculation.TryRecalculateSale(sale)) return await Reject(transaction, MoneyFailure(), ct);

            decimal applied;
            try { applied = normalized.Aggregate(0m, (sum, x) => checked(sum + x.AmountApplied)); }
            catch (OverflowException) { return await Reject(transaction, MoneyFailure(), ct); }
            if (applied > SaleCalculation.MaximumMoney) return await Reject(transaction, MoneyFailure(), ct);
            if (sale.TotalAmount == 0 && normalized.Count != 0)
                return await Reject(transaction, Conflict("A zero-total sale must not have payments."), ct);
            if (sale.TotalAmount > 0 && normalized.Count == 0)
                return await Reject(transaction, Conflict("At least one payment is required."), ct);
            if (applied < sale.TotalAmount) return await Reject(transaction, Conflict("Payment allocation is less than the sale total."), ct);
            if (applied > sale.TotalAmount) return await Reject(transaction, Conflict("Payment allocation exceeds the sale total."), ct);

            var now = clock.GetUtcNow();
            foreach (var payment in normalized)
            {
                decimal change;
                try { change = payment.Method == PaymentMethod.Cash ? SaleCalculation.Money(checked(payment.TenderedAmount - payment.AmountApplied)) : 0m; }
                catch (OverflowException) { return await Reject(transaction, MoneyFailure(), ct); }
                if (change > SaleCalculation.MaximumMoney) return await Reject(transaction, MoneyFailure(), ct);
                sale.Payments.Add(new Payment
                {
                    Method = payment.Method, AmountApplied = payment.AmountApplied,
                    TenderedAmount = payment.TenderedAmount, ChangeAmount = change,
                    ExternalReference = payment.ExternalReference, Status = PaymentStatus.Completed,
                    CreatedAtUtc = now
                });
            }
            sale.Status = SaleStatus.Completed;
            sale.ReceiptNumber = string.Format(CultureInfo.InvariantCulture, "RCP-{0:yyyyMMdd}-{1:D10}", now, sale.Id);
            sale.CompletedAtUtc = now;
            sale.UpdatedAtUtc = now;
            sale.CompletionIdempotencyKey = key;
            sale.CompletionRequestHash = hash;
            db.Entry(sale).Property(x => x.UpdatedAtUtc).IsModified = true;

            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return new(SaleOperationStatus.Success, await Detail(saleId, userId, ct));
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var completed = await db.Sales.AsNoTracking().SingleOrDefaultAsync(x => x.Id == saleId && x.CashierUserId == userId, ct);
            if (completed?.Status == SaleStatus.Completed && completed.CompletionIdempotencyKey == key && completed.CompletionRequestHash == hash)
                return new(SaleOperationStatus.Success, await Detail(saleId, userId, ct));
            return Conflict(completed?.Status == SaleStatus.Completed
                ? "Sale is already completed."
                : "The sale was modified by another request. Retry with the latest sale state.");
        }
    }

    async Task<SaleOperationResult> CompletedResult(Sale sale, string key, string hash, IDbContextTransaction? transaction, CancellationToken ct)
    {
        if (transaction is not null) await transaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        if (sale.CompletionIdempotencyKey == key && sale.CompletionRequestHash == hash)
            return new(SaleOperationStatus.Success, await Detail(sale.Id, sale.CashierUserId, ct));
        return sale.CompletionIdempotencyKey == key
            ? Conflict("Idempotency key was already used with a different completion request.")
            : Conflict("Sale is already completed.");
    }

    Task<SaleResponse?> Detail(int saleId, int userId, CancellationToken ct) =>
        SaleProjection.Project(db.Sales.AsNoTracking().Where(x => x.Id == saleId && x.CashierUserId == userId)).SingleOrDefaultAsync(ct);

    async Task<SaleOperationResult> Reject(IDbContextTransaction? transaction, SaleOperationResult result, CancellationToken ct)
    {
        if (transaction is not null) await transaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return result;
    }

    static string? NormalizeReference(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    static string RequestHash(IEnumerable<NormalizedPayment> payments)
    {
        var canonical = new StringBuilder();
        foreach (var p in payments)
        {
            var reference = p.ExternalReference ?? string.Empty;
            canonical.Append((int)p.Method).Append('|')
                .Append(p.AmountApplied.ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
                .Append(p.TenderedAmount.ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
                .Append(reference.Length).Append(':').Append(reference).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    sealed record NormalizedPayment(PaymentMethod Method, decimal AmountApplied, decimal TenderedAmount, string? ExternalReference);
    static SaleOperationResult NotFound(string message) => new(SaleOperationStatus.NotFound, null, message);
    static SaleOperationResult Conflict(string message) => new(SaleOperationStatus.Conflict, null, message);
    static SaleOperationResult Forbidden(string message) => new(SaleOperationStatus.Forbidden, null, message);
    static SaleOperationResult MoneyFailure() => new(SaleOperationStatus.BadRequest, null, "The requested calculation exceeds the supported monetary precision.");
}
