using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RetailPOSApi.Domain;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Tests;

public sealed class DatabaseModelTests
{
    private readonly IModel _model = CreateModel();

    [Theory]
    [InlineData(typeof(User), "NormalizedEmail")]
    [InlineData(typeof(Branch), "Code")]
    [InlineData(typeof(Product), "Sku")]
    public void Required_single_property_indexes_are_unique(Type entityType, string property)
    {
        var index = Entity(entityType).GetIndexes().Single(x => x.Properties.Select(p => p.Name).SequenceEqual([property]));
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Product_barcode_is_filtered_unique()
    {
        var index = Index<Product>(nameof(Product.Barcode));
        Assert.True(index.IsUnique);
        Assert.Equal("[Barcode] IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void Register_code_is_unique_within_branch()
    {
        var index = Index<Register>(nameof(Register.BranchId), nameof(Register.Code));
        Assert.True(index.IsUnique);
    }

    [Theory]
    [InlineData("UX_CashierShifts_Open_Register", "RegisterId")]
    [InlineData("UX_CashierShifts_Open_Cashier", "CashierUserId")]
    public void Open_shift_indexes_are_filtered_unique(string databaseName, string property)
    {
        var index = Entity<CashierShift>().GetIndexes().Single(x => x.GetDatabaseName() == databaseName);
        Assert.True(index.IsUnique);
        Assert.Equal([property], index.Properties.Select(x => x.Name));
        Assert.Equal("[Status] = 1", index.GetFilter());
    }

    [Fact]
    public void Nullable_receipt_number_is_filtered_unique()
    {
        var index = Index<Sale>(nameof(Sale.ReceiptNumber));
        Assert.True(index.IsUnique);
        Assert.Equal("[ReceiptNumber] IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void Sale_row_version_is_store_generated_concurrency_token()
    {
        var property = Entity<Sale>().FindProperty(nameof(Sale.RowVersion))!;
        Assert.True(property.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, property.ValueGenerated);
    }

    [Theory]
    [InlineData(typeof(Product), "UnitPrice", "decimal(18,2)")]
    [InlineData(typeof(Sale), "TotalAmount", "decimal(18,2)")]
    [InlineData(typeof(SaleLine), "LineTotal", "decimal(18,2)")]
    [InlineData(typeof(TaxRate), "Percentage", "decimal(9,4)")]
    public void Decimal_precision_is_explicit(Type entityType, string property, string columnType) =>
        Assert.Equal(columnType, Entity(entityType).FindProperty(property)!.GetColumnType());

    [Theory]
    [InlineData(typeof(SaleLine), "SaleId", DeleteBehavior.Cascade)]
    [InlineData(typeof(SaleLine), "ProductId", DeleteBehavior.Restrict)]
    [InlineData(typeof(Refund), "SaleId", DeleteBehavior.Restrict)]
    [InlineData(typeof(RefundLine), "SaleLineId", DeleteBehavior.Restrict)]
    [InlineData(typeof(RefundPayment), "OriginalPaymentId", DeleteBehavior.Restrict)]
    public void Important_foreign_keys_have_explicit_delete_behavior(Type entityType, string property, DeleteBehavior expected)
    {
        var foreignKey = Entity(entityType).GetForeignKeys().Single(x => x.Properties.Single().Name == property);
        Assert.Equal(expected, foreignKey.DeleteBehavior);
    }

    [Theory]
    [InlineData(typeof(Product), "CK_Products_UnitPrice")]
    [InlineData(typeof(TaxRate), "CK_TaxRates_Percentage")]
    [InlineData(typeof(CashierShift), "CK_CashierShifts_OpeningFloat")]
    [InlineData(typeof(SaleLine), "CK_SaleLines_Quantity")]
    [InlineData(typeof(RefundLine), "CK_RefundLines_Quantity")]
    public void Important_check_constraints_exist(Type entityType, string constraintName) =>
        Assert.Contains(Entity(entityType).GetCheckConstraints(), x => x.Name == constraintName);

    private IEntityType Entity<T>() => Entity(typeof(T));
    private IEntityType Entity(Type type) => _model.FindEntityType(type)!;

    private IIndex Index<T>(params string[] properties) => Entity<T>().GetIndexes()
        .Single(x => x.Properties.Select(p => p.Name).SequenceEqual(properties));

    private static IModel CreateModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=RetailPOSApiModelTests;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var context = new AppDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }
}
