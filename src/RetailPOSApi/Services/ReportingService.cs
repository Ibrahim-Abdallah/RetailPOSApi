using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Reports;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public interface IReportingService
{
    Task<SalesSummaryResponse> GetSalesSummary(ReportQuery query, CancellationToken cancellationToken);
    Task<ShiftSummaryResponse> GetShiftSummary(ReportQuery query, CancellationToken cancellationToken);
}

public sealed class ReportingService(AppDbContext db) : IReportingService
{
    public async Task<SalesSummaryResponse> GetSalesSummary(ReportQuery query, CancellationToken cancellationToken)
    {
        var parameters = Parameters(query);
        var saleFilter = Filter("s", "s.CompletedAtUtc", query);
        var voidFilter = Filter("s", "s.VoidedAtUtc", query);
        var refundFilter = Filter("s", "r.CreatedAtUtc", query, "Refund");

        var completionSql = $"""
            SELECT COUNT(*) AS CompletedSalesCount,
                   COALESCE(SUM(s.Subtotal), 0) AS GrossSales,
                   COALESCE(SUM(s.DiscountTotal), 0) AS DiscountTotal,
                   COALESCE(SUM(s.TaxTotal), 0) AS TaxTotal,
                   COALESCE(SUM(s.TotalAmount), 0) AS SalesTotal
            FROM Sales s
            WHERE s.CompletedAtUtc IS NOT NULL {saleFilter}
            """;
        var voidSql = $"""
            SELECT COALESCE(SUM(s.TotalAmount), 0)
            FROM Sales s
            WHERE s.Status = @VoidedStatus AND s.VoidedAtUtc IS NOT NULL {voidFilter}
            """;
        var refundSql = $"""
            SELECT COALESCE(SUM(r.TotalAmount), 0)
            FROM Refunds r INNER JOIN Sales s ON s.Id = r.SaleId
            WHERE r.Status = @CompletedRefundStatus {refundFilter}
            """;
        var paymentSql = $"""
            SELECT COALESCE(SUM(CASE WHEN p.Method = @CashMethod THEN p.AmountApplied ELSE 0 END), 0) AS CashPayments,
                   COALESCE(SUM(CASE WHEN p.Method = @CardMethod THEN p.AmountApplied ELSE 0 END), 0) AS CardPayments,
                   COALESCE(SUM(CASE WHEN p.Method = @OtherMethod THEN p.AmountApplied ELSE 0 END), 0) AS OtherPayments
            FROM Payments p INNER JOIN Sales s ON s.Id = p.SaleId
            WHERE p.Status = @CompletedPaymentStatus AND s.CompletedAtUtc IS NOT NULL {saleFilter}
            """;
        var productsSql = $"""
            SELECT sl.ProductId,
                   MIN(sl.ProductSku) AS ProductSku,
                   MIN(sl.ProductName) AS ProductName,
                   SUM(sl.Quantity) AS QuantitySold,
                   COALESCE(SUM(sl.LineTotal), 0) AS SalesTotal
            FROM SaleLines sl INNER JOIN Sales s ON s.Id = sl.SaleId
            WHERE s.CompletedAtUtc IS NOT NULL {saleFilter}
            GROUP BY sl.ProductId
            ORDER BY SalesTotal DESC, QuantitySold DESC, sl.ProductId ASC
            """;

        var connection = db.Database.GetDbConnection();
        var completion = await connection.QuerySingleAsync<CompletionTotals>(Command(completionSql, parameters, cancellationToken));
        var voidTotal = await connection.ExecuteScalarAsync<decimal>(Command(voidSql, parameters, cancellationToken));
        var refundTotal = await connection.ExecuteScalarAsync<decimal>(Command(refundSql, parameters, cancellationToken));
        var payments = await connection.QuerySingleAsync<PaymentTotals>(Command(paymentSql, parameters, cancellationToken));
        var products = (await connection.QueryAsync<TopProductRow>(Command(productsSql, parameters, cancellationToken)))
            .Take(5).Select(x => new TopProductResponse(x.ProductId, x.ProductSku, x.ProductName, x.QuantitySold, x.SalesTotal)).ToArray();

        return new(completion.CompletedSalesCount, completion.GrossSales, completion.DiscountTotal,
            completion.TaxTotal, completion.SalesTotal, voidTotal, refundTotal,
            completion.SalesTotal - voidTotal - refundTotal, payments.CashPayments,
            payments.CardPayments, payments.OtherPayments, products);
    }

    public async Task<ShiftSummaryResponse> GetShiftSummary(ReportQuery query, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(*) AS ClosedShiftCount,
                   COALESCE(SUM(cs.OpeningFloat), 0) AS OpeningFloatTotal,
                   COALESCE(SUM(cs.DeclaredCash), 0) AS DeclaredCashTotal,
                   COALESCE(SUM(cs.ExpectedCash), 0) AS ExpectedCashTotal,
                   COALESCE(SUM(cs.CashVariance), 0) AS CashVarianceTotal,
                   COALESCE(SUM(CASE WHEN cs.CashVariance > 0 THEN cs.CashVariance ELSE 0 END), 0) AS TotalOverage,
                   COALESCE(SUM(CASE WHEN cs.CashVariance < 0 THEN -cs.CashVariance ELSE 0 END), 0) AS TotalShortage
            FROM CashierShifts cs
            WHERE cs.Status = @ClosedShiftStatus AND cs.ClosedAtUtc IS NOT NULL {Filter("cs", "cs.ClosedAtUtc", query)}
            """;
        var totals = await db.Database.GetDbConnection().QuerySingleAsync<ShiftTotals>(
            Command(sql, Parameters(query), cancellationToken));
        return new(totals.ClosedShiftCount, totals.OpeningFloatTotal, totals.DeclaredCashTotal,
            totals.ExpectedCashTotal, totals.CashVarianceTotal, totals.TotalOverage, totals.TotalShortage);
    }

    private DynamicParameters Parameters(ReportQuery query)
    {
        var sqlite = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
        var parameters = new DynamicParameters();
        parameters.Add("FromDate", Date(query.FromDate));
        parameters.Add("ToDate", Date(query.ToDate));
        parameters.Add("RefundFromDate", sqlite ? query.FromDate?.ToUniversalTime().UtcTicks : Date(query.FromDate));
        parameters.Add("RefundToDate", sqlite ? query.ToDate?.ToUniversalTime().UtcTicks : Date(query.ToDate));
        parameters.Add("BranchId", query.BranchId);
        parameters.Add("RegisterId", query.RegisterId);
        parameters.Add("CashierUserId", query.CashierUserId);
        parameters.Add("VoidedStatus", (int)SaleStatus.Voided);
        parameters.Add("CompletedRefundStatus", (int)RefundStatus.Completed);
        parameters.Add("CompletedPaymentStatus", (int)PaymentStatus.Completed);
        parameters.Add("ClosedShiftStatus", (int)CashierShiftStatus.Closed);
        parameters.Add("CashMethod", (int)PaymentMethod.Cash);
        parameters.Add("CardMethod", (int)PaymentMethod.Card);
        parameters.Add("OtherMethod", (int)PaymentMethod.Other);
        return parameters;
    }

    private static DateTimeOffset? Date(DateTimeOffset? value) => value?.ToUniversalTime();

    private static string Filter(string alias, string timestamp, ReportQuery query, string parameterPrefix = "")
    {
        var sql = new List<string>();
        if (query.FromDate.HasValue) sql.Add($"AND {timestamp} >= @{parameterPrefix}FromDate");
        if (query.ToDate.HasValue) sql.Add($"AND {timestamp} < @{parameterPrefix}ToDate");
        if (query.BranchId.HasValue) sql.Add($"AND {alias}.BranchId = @BranchId");
        if (query.RegisterId.HasValue) sql.Add($"AND {alias}.RegisterId = @RegisterId");
        if (query.CashierUserId.HasValue) sql.Add($"AND {alias}.CashierUserId = @CashierUserId");
        return string.Join(' ', sql);
    }

    private static CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, cancellationToken: cancellationToken);

    private sealed class CompletionTotals
    {
        public long CompletedSalesCount { get; set; }
        public decimal GrossSales { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal SalesTotal { get; set; }
    }
    private sealed class PaymentTotals
    {
        public decimal CashPayments { get; set; }
        public decimal CardPayments { get; set; }
        public decimal OtherPayments { get; set; }
    }
    private sealed class TopProductRow
    {
        public int ProductId { get; set; }
        public string ProductSku { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public int QuantitySold { get; set; }
        public decimal SalesTotal { get; set; }
    }
    private sealed class ShiftTotals
    {
        public long ClosedShiftCount { get; set; }
        public decimal OpeningFloatTotal { get; set; }
        public decimal DeclaredCashTotal { get; set; }
        public decimal ExpectedCashTotal { get; set; }
        public decimal CashVarianceTotal { get; set; }
        public decimal TotalOverage { get; set; }
        public decimal TotalShortage { get; set; }
    }
}
