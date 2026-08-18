using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Register> Registers => Set<Register>();
    public DbSet<CashierShift> CashierShifts => Set<CashierShift>();
    public DbSet<TaxRate> TaxRates => Set<TaxRate>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundLine> RefundLines => Set<RefundLine>();
    public DbSet<RefundPayment> RefundPayments => Set<RefundPayment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
