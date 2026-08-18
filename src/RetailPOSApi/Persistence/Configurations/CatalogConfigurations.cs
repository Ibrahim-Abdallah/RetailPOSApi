using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Persistence.Configurations;

internal static class ConfigurationHelpers
{
    public const string Money = "decimal(18,2)";
    public const string Percentage = "decimal(9,4)";
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.LastName).HasMaxLength(100);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.NormalizedEmail).HasMaxLength(320);
        b.Property(x => x.PasswordHash).HasMaxLength(500);
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(128);
        b.Property(x => x.ReplacedByTokenHash).HasMaxLength(128);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.UserId, x.ExpiresAtUtc });
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.Address).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class RegisterConfiguration : IEntityTypeConfiguration<Register>
{
    public void Configure(EntityTypeBuilder<Register> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Code).HasMaxLength(50);
        b.HasIndex(x => new { x.BranchId, x.Code }).IsUnique();
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Percentage).HasColumnType(ConfigurationHelpers.Percentage);
        b.ToTable(t => t.HasCheckConstraint("CK_TaxRates_Percentage", "[Percentage] >= 0 AND [Percentage] <= 100"));
    }
}

public sealed class DiscountConfiguration : IEntityTypeConfiguration<Discount>
{
    public void Configure(EntityTypeBuilder<Discount> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Value).HasColumnType(ConfigurationHelpers.Percentage);
        b.ToTable(t => t.HasCheckConstraint("CK_Discounts_Value", "[Value] >= 0"));
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.Property(x => x.Sku).HasMaxLength(100);
        b.Property(x => x.Barcode).HasMaxLength(100);
        b.Property(x => x.Name).HasMaxLength(300);
        b.Property(x => x.UnitPrice).HasColumnType(ConfigurationHelpers.Money);
        b.HasIndex(x => x.Sku).IsUnique();
        b.HasIndex(x => x.Barcode).IsUnique().HasFilter("[Barcode] IS NOT NULL");
        b.HasOne(x => x.TaxRate).WithMany().HasForeignKey(x => x.TaxRateId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_Products_UnitPrice", "[UnitPrice] >= 0"));
    }
}
