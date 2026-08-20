using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public sealed record LifecycleResult<T>(SaleOperationStatus Status, T? Value = default, string? Message = null);

public interface ISaleLifecycleService
{
    Task<LifecycleResult<SaleResponse>> Void(int saleId, VoidSaleRequest request, CancellationToken ct);
    Task<LifecycleResult<RefundResponse>> Refund(int saleId, ProcessRefundRequest request, CancellationToken ct);
    Task<LifecycleResult<IReadOnlyList<RefundResponse>>> Refunds(int saleId, CancellationToken ct);
}

public sealed class SaleLifecycleService(AppDbContext db, ICurrentUserService currentUser, TimeProvider clock) : ISaleLifecycleService
{
    public async Task<LifecycleResult<SaleResponse>> Void(int saleId, VoidSaleRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not int userId) return Forbidden<SaleResponse>("Authenticated management identity is unavailable.");
        await using var transaction = await BeginTransaction(ct);
        var sale = await db.Sales.SingleOrDefaultAsync(x => x.Id == saleId, ct);
        if (sale is null) return NotFound<SaleResponse>();
        if (sale.Status != SaleStatus.Completed) return Conflict<SaleResponse>("Only a completed, unrefunded sale can be voided.");
        if (await db.Refunds.AnyAsync(x => x.SaleId == saleId, ct)) return Conflict<SaleResponse>("A sale with refund history cannot be voided.");

        var now = clock.GetUtcNow();
        sale.Status = SaleStatus.Voided; sale.VoidedAtUtc = now; sale.VoidedByUserId = userId;
        sale.VoidReason = request.Reason.Trim(); sale.UpdatedAtUtc = now;
        db.Entry(sale).Property(x => x.UpdatedAtUtc).IsModified = true;
        var failure = await Save(transaction, ct);
        if (failure is not null) return new(failure.Value.Status, null, failure.Value.Message);
        return new(SaleOperationStatus.Success, await SaleProjection.Project(db.Sales.AsNoTracking().Where(x => x.Id == saleId)).SingleAsync(ct));
    }

    public async Task<LifecycleResult<RefundResponse>> Refund(int saleId, ProcessRefundRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not int userId) return Forbidden<RefundResponse>("Authenticated management identity is unavailable.");
        await using var transaction = await BeginTransaction(ct);
        var sale = await db.Sales.Include(x => x.Lines).Include(x => x.Payments).SingleOrDefaultAsync(x => x.Id == saleId, ct);
        if (sale is null) return NotFound<RefundResponse>();
        if (sale.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded))
            return Conflict<RefundResponse>("Only a completed or partially refunded sale can be refunded.");

        var requestedLineIds = request.Lines.Select(x => x.SaleLineId).ToArray();
        if (requestedLineIds.Any(id => sale.Lines.All(x => x.Id != id))) return Conflict<RefundResponse>("A refund line does not belong to this sale.");
        var saleLineIds = sale.Lines.Select(x => x.Id).ToArray();
        var priorQuantities = await db.RefundLines.Where(x => saleLineIds.Contains(x.SaleLineId) && x.Refund.Status == RefundStatus.Completed)
            .GroupBy(x => x.SaleLineId).Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.Quantity) }).ToDictionaryAsync(x => x.Id, x => x.Quantity, ct);

        var now = clock.GetUtcNow();
        var refund = new Refund { SaleId = saleId, ProcessedByUserId = userId, Status = RefundStatus.Completed,
            Reason = request.Reason.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now };
        try
        {
            foreach (var requested in request.Lines)
            {
                var line = sale.Lines.Single(x => x.Id == requested.SaleLineId);
                var prior = priorQuantities.GetValueOrDefault(line.Id);
                if (requested.Quantity > line.Quantity - prior) return Conflict<RefundResponse>("Refund quantity exceeds the remaining refundable quantity.");
                refund.Lines.Add(new RefundLine { SaleLineId = line.Id, Quantity = requested.Quantity,
                    Subtotal = SaleCalculation.Money(checked(line.UnitPrice * requested.Quantity)),
                    DiscountTotal = SaleCalculation.Money(checked(line.UnitDiscountAmount * requested.Quantity)),
                    TaxTotal = SaleCalculation.Money(checked(line.UnitTaxAmount * requested.Quantity)),
                    TotalAmount = SaleCalculation.Money(checked(line.UnitTotal * requested.Quantity)) });
            }
            refund.Subtotal = SaleCalculation.Money(refund.Lines.Aggregate(0m, (sum, x) => checked(sum + x.Subtotal)));
            refund.DiscountTotal = SaleCalculation.Money(refund.Lines.Aggregate(0m, (sum, x) => checked(sum + x.DiscountTotal)));
            refund.TaxTotal = SaleCalculation.Money(refund.Lines.Aggregate(0m, (sum, x) => checked(sum + x.TaxTotal)));
            refund.TotalAmount = SaleCalculation.Money(refund.Lines.Aggregate(0m, (sum, x) => checked(sum + x.TotalAmount)));
        }
        catch (OverflowException) { return Conflict<RefundResponse>("Refund calculation exceeds the supported monetary precision."); }

        decimal allocation;
        try { allocation = request.Payments.Aggregate(0m, (sum, x) => checked(sum + x.Amount)); }
        catch (OverflowException) { return Conflict<RefundResponse>("Refund payment allocation exceeds the supported monetary precision."); }
        if (allocation != refund.TotalAmount) return Conflict<RefundResponse>("Refund payment allocation must equal the refund total.");
        if (refund.TotalAmount == 0 && request.Payments.Count != 0) return Conflict<RefundResponse>("A zero-total refund must not have refund payments.");

        var paymentIds = request.Payments.Select(x => x.OriginalPaymentId).ToArray();
        if (paymentIds.Any(id => sale.Payments.All(x => x.Id != id))) return Conflict<RefundResponse>("An original payment does not belong to this sale.");
        var priorAllocations = await db.RefundPayments.Where(x => paymentIds.Contains(x.OriginalPaymentId) && x.Refund.Status == RefundStatus.Completed)
            .GroupBy(x => x.OriginalPaymentId).Select(x => new { Id = x.Key, Amount = x.Sum(y => y.Amount) }).ToDictionaryAsync(x => x.Id, x => x.Amount, ct);
        foreach (var requested in request.Payments)
        {
            var payment = sale.Payments.Single(x => x.Id == requested.OriginalPaymentId);
            if (payment.Status != PaymentStatus.Completed) return Conflict<RefundResponse>("The original payment is not completed.");
            if (priorAllocations.GetValueOrDefault(payment.Id) + requested.Amount > payment.AmountApplied)
                return Conflict<RefundResponse>("Refund allocation exceeds the original payment's remaining capacity.");
            refund.Payments.Add(new RefundPayment { OriginalPaymentId = payment.Id, Method = payment.Method, Amount = requested.Amount,
                ExternalReference = Normalize(requested.ExternalReference), CreatedAtUtc = now });
        }

        db.Refunds.Add(refund);
        var newQuantities = request.Lines.ToDictionary(x => x.SaleLineId, x => x.Quantity);
        sale.Status = sale.Lines.All(x => priorQuantities.GetValueOrDefault(x.Id) + newQuantities.GetValueOrDefault(x.Id) == x.Quantity)
            ? SaleStatus.Refunded : SaleStatus.PartiallyRefunded;
        sale.UpdatedAtUtc = now; db.Entry(sale).Property(x => x.UpdatedAtUtc).IsModified = true;
        var failure = await Save(transaction, ct);
        if (failure is not null) return new(failure.Value.Status, null, failure.Value.Message);
        return new(SaleOperationStatus.Success, await ProjectRefund(db.Refunds.AsNoTracking().Where(x => x.Id == refund.Id)).SingleAsync(ct));
    }

    public async Task<LifecycleResult<IReadOnlyList<RefundResponse>>> Refunds(int saleId, CancellationToken ct)
    {
        if (!await db.Sales.AsNoTracking().AnyAsync(x => x.Id == saleId, ct)) return NotFound<IReadOnlyList<RefundResponse>>();
        var values = await ProjectRefund(db.Refunds.AsNoTracking().Where(x => x.SaleId == saleId && x.Status == RefundStatus.Completed)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)).ToListAsync(ct);
        return new(SaleOperationStatus.Success, values);
    }

    async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransaction(CancellationToken ct) =>
        db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite" ? null : await db.Database.BeginTransactionAsync(ct);

    async Task<(SaleOperationStatus Status, string Message)?> Save(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); if (transaction is not null) await transaction.CommitAsync(ct); return null; }
        catch (DbUpdateConcurrencyException) { if (transaction is not null) await transaction.RollbackAsync(ct); db.ChangeTracker.Clear(); return (SaleOperationStatus.Conflict, "The sale was modified by another lifecycle request. Retry with the latest sale state."); }
    }

    static IQueryable<RefundResponse> ProjectRefund(IQueryable<Refund> source) => source.Select(x => new RefundResponse(
        x.Id, x.SaleId, x.ProcessedByUserId, x.ProcessedByUser.FirstName + " " + x.ProcessedByUser.LastName, x.Status,
        x.Subtotal, x.DiscountTotal, x.TaxTotal, x.TotalAmount, x.Reason, x.CreatedAtUtc, x.UpdatedAtUtc,
        x.Lines.OrderBy(y => y.Id).Select(y => new RefundLineResponse(y.Id, y.SaleLineId, y.SaleLine.ProductId,
            y.SaleLine.ProductSku, y.SaleLine.ProductName, y.Quantity, y.Subtotal, y.DiscountTotal, y.TaxTotal, y.TotalAmount)).ToList(),
        x.Payments.OrderBy(y => y.Id).Select(y => new RefundPaymentResponse(y.Id, y.OriginalPaymentId, y.Method, y.Amount, y.ExternalReference, y.CreatedAtUtc)).ToList()));
    static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    static LifecycleResult<T> NotFound<T>() => new(SaleOperationStatus.NotFound, default, "Sale not found.");
    static LifecycleResult<T> Conflict<T>(string message) => new(SaleOperationStatus.Conflict, default, message);
    static LifecycleResult<T> Forbidden<T>(string message) => new(SaleOperationStatus.Forbidden, default, message);
}
