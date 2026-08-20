namespace RetailPOSApi.Domain;

public sealed class User
{
    public int Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public User User { get; set; } = null!;
}

public sealed class Branch
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public required string Address { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class Register
{
    public int Id { get; set; }
    public int BranchId { get; set; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Branch Branch { get; set; } = null!;
}

public sealed class CashierShift
{
    public int Id { get; set; }
    public int BranchId { get; set; }
    public int RegisterId { get; set; }
    public int CashierUserId { get; set; }
    public CashierShiftStatus Status { get; set; }
    public decimal OpeningFloat { get; set; }
    public DateTimeOffset OpenedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public decimal? DeclaredCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? CashVariance { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Branch Branch { get; set; } = null!;
    public Register Register { get; set; } = null!;
    public User CashierUser { get; set; } = null!;
}

public sealed class TaxRate
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Percentage { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class Discount
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DiscountType Type { get; set; }
    public decimal Value { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class Product
{
    public int Id { get; set; }
    public required string Sku { get; set; }
    public string? Barcode { get; set; }
    public required string Name { get; set; }
    public decimal UnitPrice { get; set; }
    public int TaxRateId { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public TaxRate TaxRate { get; set; } = null!;
}

public sealed class Sale
{
    public int Id { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? CompletionIdempotencyKey { get; set; }
    public string? CompletionRequestHash { get; set; }
    public int BranchId { get; set; }
    public int RegisterId { get; set; }
    public int CashierShiftId { get; set; }
    public int CashierUserId { get; set; }
    public SaleStatus Status { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? VoidedAtUtc { get; set; }
    public int? VoidedByUserId { get; set; }
    public string? VoidReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public Branch Branch { get; set; } = null!;
    public Register Register { get; set; } = null!;
    public CashierShift CashierShift { get; set; } = null!;
    public User CashierUser { get; set; } = null!;
    public User? VoidedByUser { get; set; }
    public ICollection<SaleLine> Lines { get; set; } = [];
    public ICollection<Payment> Payments { get; set; } = [];
}

public sealed class SaleLine
{
    public int Id { get; set; }
    public int SaleId { get; set; }
    public int ProductId { get; set; }
    public required string ProductSku { get; set; }
    public required string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public int? DiscountId { get; set; }
    public string? DiscountName { get; set; }
    public DiscountType? DiscountType { get; set; }
    public decimal? DiscountValue { get; set; }
    public decimal UnitDiscountAmount { get; set; }
    public decimal UnitNetAmount { get; set; }
    public int TaxRateId { get; set; }
    public required string TaxRateName { get; set; }
    public decimal TaxRatePercentage { get; set; }
    public decimal UnitTaxAmount { get; set; }
    public decimal UnitTotal { get; set; }
    public decimal LineSubtotal { get; set; }
    public decimal LineDiscountTotal { get; set; }
    public decimal LineTaxTotal { get; set; }
    public decimal LineTotal { get; set; }
    public Sale Sale { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public Discount? Discount { get; set; }
    public TaxRate TaxRate { get; set; } = null!;
}

public sealed class Payment
{
    public int Id { get; set; }
    public int SaleId { get; set; }
    public PaymentMethod Method { get; set; }
    public decimal AmountApplied { get; set; }
    public decimal TenderedAmount { get; set; }
    public decimal ChangeAmount { get; set; }
    public string? ExternalReference { get; set; }
    public PaymentStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Sale Sale { get; set; } = null!;
}

public sealed class Refund
{
    public int Id { get; set; }
    public int SaleId { get; set; }
    public int ProcessedByUserId { get; set; }
    public RefundStatus Status { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal TotalAmount { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Sale Sale { get; set; } = null!;
    public User ProcessedByUser { get; set; } = null!;
    public ICollection<RefundLine> Lines { get; set; } = [];
    public ICollection<RefundPayment> Payments { get; set; } = [];
}

public sealed class RefundLine
{
    public int Id { get; set; }
    public int RefundId { get; set; }
    public int SaleLineId { get; set; }
    public int Quantity { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal TotalAmount { get; set; }
    public Refund Refund { get; set; } = null!;
    public SaleLine SaleLine { get; set; } = null!;
}

public sealed class RefundPayment
{
    public int Id { get; set; }
    public int RefundId { get; set; }
    public int OriginalPaymentId { get; set; }
    public PaymentMethod Method { get; set; }
    public decimal Amount { get; set; }
    public string? ExternalReference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Refund Refund { get; set; } = null!;
    public Payment OriginalPayment { get; set; } = null!;
}
