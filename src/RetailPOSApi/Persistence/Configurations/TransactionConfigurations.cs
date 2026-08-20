using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Persistence.Configurations;

public sealed class CashierShiftConfiguration : IEntityTypeConfiguration<CashierShift>
{
    public void Configure(EntityTypeBuilder<CashierShift> b)
    {
        b.Property(x => x.OpeningFloat).HasColumnType(ConfigurationHelpers.Money);
        b.Property(x => x.DeclaredCash).HasColumnType(ConfigurationHelpers.Money);
        b.Property(x => x.ExpectedCash).HasColumnType(ConfigurationHelpers.Money);
        b.Property(x => x.CashVariance).HasColumnType(ConfigurationHelpers.Money);
        b.HasIndex(x => x.RegisterId).IsUnique().HasFilter($"[Status] = {(int)CashierShiftStatus.Open}").HasDatabaseName("UX_CashierShifts_Open_Register");
        b.HasIndex(x => x.CashierUserId).IsUnique().HasFilter($"[Status] = {(int)CashierShiftStatus.Open}").HasDatabaseName("UX_CashierShifts_Open_Cashier");
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Register).WithMany().HasForeignKey(x => x.RegisterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CashierUser).WithMany().HasForeignKey(x => x.CashierUserId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_CashierShifts_OpeningFloat", "[OpeningFloat] >= 0"));
    }
}

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        b.Property(x => x.ReceiptNumber).HasMaxLength(100);
        b.Property(x => x.CompletionIdempotencyKey).HasMaxLength(100);
        b.Property(x => x.CompletionRequestHash).HasMaxLength(64);
        b.Property(x => x.VoidReason).HasMaxLength(500);
        foreach (var name in new[] { nameof(Sale.Subtotal), nameof(Sale.DiscountTotal), nameof(Sale.TaxTotal), nameof(Sale.TotalAmount) })
            b.Property(name).HasColumnType(ConfigurationHelpers.Money);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.ReceiptNumber).IsUnique().HasFilter("[ReceiptNumber] IS NOT NULL");
        b.HasIndex(x => x.CashierShiftId);
        b.HasIndex(x => new { x.BranchId, x.CreatedAtUtc });
        b.HasIndex(x => new { x.RegisterId, x.CreatedAtUtc });
        b.HasIndex(x => new { x.CashierUserId, x.CreatedAtUtc });
        b.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Register).WithMany().HasForeignKey(x => x.RegisterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CashierShift).WithMany().HasForeignKey(x => x.CashierShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CashierUser).WithMany().HasForeignKey(x => x.CashierUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.VoidedByUser).WithMany().HasForeignKey(x => x.VoidedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_Sales_Totals", "[Subtotal] >= 0 AND [DiscountTotal] >= 0 AND [TaxTotal] >= 0 AND [TotalAmount] >= 0"));
    }
}

public sealed class SaleLineConfiguration : IEntityTypeConfiguration<SaleLine>
{
    public void Configure(EntityTypeBuilder<SaleLine> b)
    {
        b.Property(x => x.ProductSku).HasMaxLength(100);
        b.Property(x => x.ProductName).HasMaxLength(300);
        b.Property(x => x.DiscountName).HasMaxLength(200);
        b.Property(x => x.TaxRateName).HasMaxLength(200);
        b.Property(x => x.DiscountValue).HasColumnType(ConfigurationHelpers.Percentage);
        b.Property(x => x.TaxRatePercentage).HasColumnType(ConfigurationHelpers.Percentage);
        foreach (var name in new[] { nameof(SaleLine.UnitPrice), nameof(SaleLine.UnitDiscountAmount), nameof(SaleLine.UnitNetAmount), nameof(SaleLine.UnitTaxAmount), nameof(SaleLine.UnitTotal), nameof(SaleLine.LineSubtotal), nameof(SaleLine.LineDiscountTotal), nameof(SaleLine.LineTaxTotal), nameof(SaleLine.LineTotal) })
            b.Property(name).HasColumnType(ConfigurationHelpers.Money);
        b.HasOne(x => x.Sale).WithMany(x => x.Lines).HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Discount).WithMany().HasForeignKey(x => x.DiscountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.TaxRate).WithMany().HasForeignKey(x => x.TaxRateId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_SaleLines_Quantity", "[Quantity] > 0"));
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.Property(x => x.ExternalReference).HasMaxLength(200);
        foreach (var name in new[] { nameof(Payment.AmountApplied), nameof(Payment.TenderedAmount), nameof(Payment.ChangeAmount) })
            b.Property(name).HasColumnType(ConfigurationHelpers.Money);
        b.HasOne(x => x.Sale).WithMany(x => x.Payments).HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
        b.ToTable(t => t.HasCheckConstraint("CK_Payments_Amounts", "[AmountApplied] >= 0 AND [TenderedAmount] >= 0 AND [ChangeAmount] >= 0"));
    }
}
