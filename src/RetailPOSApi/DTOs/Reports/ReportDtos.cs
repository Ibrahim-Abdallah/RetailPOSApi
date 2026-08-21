namespace RetailPOSApi.DTOs.Reports;

public sealed record ReportQuery(
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate,
    int? BranchId,
    int? RegisterId,
    int? CashierUserId);

public sealed record TopProductResponse(
    int ProductId,
    string ProductSku,
    string ProductName,
    int QuantitySold,
    decimal SalesTotal);

public sealed record SalesSummaryResponse(
    long CompletedSalesCount,
    decimal GrossSales,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal SalesTotal,
    decimal VoidTotal,
    decimal RefundTotal,
    decimal NetSales,
    decimal CashPayments,
    decimal CardPayments,
    decimal OtherPayments,
    IReadOnlyList<TopProductResponse> TopProducts);

public sealed record ShiftSummaryResponse(
    long ClosedShiftCount,
    decimal OpeningFloatTotal,
    decimal DeclaredCashTotal,
    decimal ExpectedCashTotal,
    decimal CashVarianceTotal,
    decimal TotalOverage,
    decimal TotalShortage);
