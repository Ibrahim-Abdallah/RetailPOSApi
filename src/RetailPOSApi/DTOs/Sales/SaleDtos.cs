using RetailPOSApi.Domain;

namespace RetailPOSApi.DTOs.Sales;

public sealed record AddSaleLineRequest(int ProductId, int Quantity, int? DiscountId = null);
public sealed record UpdateSaleLineQuantityRequest(int Quantity);
public sealed record ApplySaleLineDiscountRequest(int DiscountId);
public sealed record CompleteSaleRequest(string IdempotencyKey, IReadOnlyList<CompleteSalePaymentRequest> Payments);
public sealed record CompleteSalePaymentRequest(PaymentMethod Method, decimal AmountApplied, decimal TenderedAmount, string? ExternalReference = null);
public sealed record VoidSaleRequest(string Reason);
public sealed record ProcessRefundRequest(string Reason, IReadOnlyList<ProcessRefundLineRequest> Lines, IReadOnlyList<ProcessRefundPaymentRequest> Payments);
public sealed record ProcessRefundLineRequest(int SaleLineId, int Quantity);
public sealed record ProcessRefundPaymentRequest(int OriginalPaymentId, decimal Amount, string? ExternalReference = null);

public record SaleQuery(
    int Page = 1,
    int PageSize = 20,
    SaleStatus? Status = null,
    int? CashierShiftId = null,
    string SortBy = "createdAt",
    string SortDirection = "desc");

public sealed record ManagementSaleQuery(
    int Page = 1,
    int PageSize = 20,
    SaleStatus? Status = null,
    int? CashierShiftId = null,
    string SortBy = "createdAt",
    string SortDirection = "desc",
    int? BranchId = null,
    int? RegisterId = null,
    int? CashierUserId = null) : SaleQuery(Page, PageSize, Status, CashierShiftId, SortBy, SortDirection);

public sealed record SaleLineResponse(
    int Id, int ProductId, string ProductSku, string ProductName, int Quantity, decimal UnitPrice,
    int? DiscountId, string? DiscountName, DiscountType? DiscountType, decimal? DiscountValue,
    decimal UnitDiscountAmount, decimal UnitNetAmount,
    int TaxRateId, string TaxRateName, decimal TaxRatePercentage,
    decimal UnitTaxAmount, decimal UnitTotal,
    decimal LineSubtotal, decimal LineDiscountTotal, decimal LineTaxTotal, decimal LineTotal);

public sealed record PaymentResponse(
    int Id, PaymentMethod Method, decimal AmountApplied, decimal TenderedAmount,
    decimal ChangeAmount, string? ExternalReference, PaymentStatus Status, DateTimeOffset CreatedAtUtc);

public sealed record SaleResponse(
    int Id, SaleStatus Status,
    int BranchId, string BranchCode, string BranchName,
    int RegisterId, string RegisterCode, string RegisterName,
    int CashierShiftId, int CashierUserId, string CashierName,
    decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal TotalAmount,
    string? ReceiptNumber, DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? VoidedAtUtc, int? VoidedByUserId, string? VoidedByName, string? VoidReason,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<SaleLineResponse> Lines,
    IReadOnlyList<PaymentResponse> Payments);

public sealed record RefundLineResponse(int Id, int SaleLineId, int ProductId, string ProductSku, string ProductName,
    int Quantity, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal TotalAmount);
public sealed record RefundPaymentResponse(int Id, int OriginalPaymentId, PaymentMethod Method, decimal Amount,
    string? ExternalReference, DateTimeOffset CreatedAtUtc);
public sealed record RefundResponse(int Id, int SaleId, int ProcessedByUserId, string ProcessedByName, RefundStatus Status,
    decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal TotalAmount, string Reason,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<RefundLineResponse> Lines, IReadOnlyList<RefundPaymentResponse> Payments);
