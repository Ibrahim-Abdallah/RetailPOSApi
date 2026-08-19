using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // SQLite cannot order DateTimeOffset values natively. The relational test
        // provider uses a sortable binary representation; SQL Server keeps its
        // native datetimeoffset mapping and therefore the production model unchanged.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            modelBuilder.Entity<Sale>().Property(x => x.RowVersion)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            var converter = new ValueConverter<DateTimeOffset, long>(
                value => value.UtcTicks,
                value => new DateTimeOffset(value, TimeSpan.Zero));
            foreach (var property in modelBuilder.Model.GetEntityTypes()
                         .SelectMany(entity => entity.GetProperties())
                         .Where(property => property.ClrType == typeof(DateTimeOffset)))
            {
                property.SetValueConverter(converter);
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareSqliteRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareSqliteRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareSqliteRowVersions()
    {
        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite") return;
        foreach (var entry in ChangeTracker.Entries<Sale>().Where(x => x.State is EntityState.Added or EntityState.Modified))
            entry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
    }
}
