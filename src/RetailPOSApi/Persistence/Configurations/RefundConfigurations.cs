using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Persistence.Configurations;

public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.Property(x => x.Reason).HasMaxLength(500);
        foreach (var name in new[] { nameof(Refund.Subtotal), nameof(Refund.DiscountTotal), nameof(Refund.TaxTotal), nameof(Refund.TotalAmount) })
            b.Property(name).HasColumnType(ConfigurationHelpers.Money);
        b.HasIndex(x => x.SaleId);
        b.HasIndex(x => x.CreatedAtUtc);
        b.HasIndex(x => new { x.SaleId, x.CreatedAtUtc });
        b.HasOne(x => x.Sale).WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ProcessedByUser).WithMany().HasForeignKey(x => x.ProcessedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_Refunds_Totals", "[Subtotal] >= 0 AND [DiscountTotal] >= 0 AND [TaxTotal] >= 0 AND [TotalAmount] >= 0"));
    }
}

public sealed class RefundLineConfiguration : IEntityTypeConfiguration<RefundLine>
{
    public void Configure(EntityTypeBuilder<RefundLine> b)
    {
        foreach (var name in new[] { nameof(RefundLine.Subtotal), nameof(RefundLine.DiscountTotal), nameof(RefundLine.TaxTotal), nameof(RefundLine.TotalAmount) })
            b.Property(name).HasColumnType(ConfigurationHelpers.Money);
        b.HasOne(x => x.Refund).WithMany(x => x.Lines).HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.SaleLine).WithMany().HasForeignKey(x => x.SaleLineId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => { t.HasCheckConstraint("CK_RefundLines_Quantity", "[Quantity] > 0"); t.HasCheckConstraint("CK_RefundLines_Totals", "[Subtotal] >= 0 AND [DiscountTotal] >= 0 AND [TaxTotal] >= 0 AND [TotalAmount] >= 0"); });
    }
}

public sealed class RefundPaymentConfiguration : IEntityTypeConfiguration<RefundPayment>
{
    public void Configure(EntityTypeBuilder<RefundPayment> b)
    {
        b.Property(x => x.Amount).HasColumnType(ConfigurationHelpers.Money);
        b.Property(x => x.ExternalReference).HasMaxLength(200);
        b.HasOne(x => x.Refund).WithMany(x => x.Payments).HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.OriginalPayment).WithMany().HasForeignKey(x => x.OriginalPaymentId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_RefundPayments_Amount", "[Amount] > 0"));
    }
}
