using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.Persistence;

namespace RetailPOSApi.Services;

public enum ConfigurationStatus
{
    Success, NotFound, Conflict
}
public sealed record ConfigurationResult<T>(ConfigurationStatus Status, T? Value = default, string? Message = null);
public interface IConfigurationService
{
    Task<ConfigurationResult<BranchResponse>> CreateBranch(BranchRequest r, CancellationToken ct);
    Task<PagedResponse<BranchResponse>> ListBranches(BranchQuery q, CancellationToken ct);
    Task<BranchResponse?> GetBranch(int id, CancellationToken ct);
    Task<ConfigurationResult<BranchResponse>> UpdateBranch(int id, BranchRequest r, CancellationToken ct);
    Task<ConfigurationResult<BranchResponse>> ActivateBranch(int id, bool active, CancellationToken ct);
    Task<ConfigurationResult<RegisterResponse>> CreateRegister(CreateRegisterRequest r, CancellationToken ct);
    Task<PagedResponse<RegisterResponse>> ListRegisters(RegisterQuery q, CancellationToken ct);
    Task<RegisterResponse?> GetRegister(int id, CancellationToken ct);
    Task<ConfigurationResult<RegisterResponse>> UpdateRegister(int id, UpdateRegisterRequest r, CancellationToken ct);
    Task<ConfigurationResult<RegisterResponse>> ActivateRegister(int id, bool active, CancellationToken ct);
    Task<ConfigurationResult<TaxRateResponse>> CreateTaxRate(TaxRateRequest r, CancellationToken ct);
    Task<PagedResponse<TaxRateResponse>> ListTaxRates(TaxRateQuery q, CancellationToken ct);
    Task<TaxRateResponse?> GetTaxRate(int id, CancellationToken ct);
    Task<ConfigurationResult<TaxRateResponse>> UpdateTaxRate(int id, TaxRateRequest r, CancellationToken ct);
    Task<ConfigurationResult<TaxRateResponse>> ActivateTaxRate(int id, bool active, CancellationToken ct);
    Task<ConfigurationResult<DiscountResponse>> CreateDiscount(DiscountRequest r, CancellationToken ct);
    Task<PagedResponse<DiscountResponse>> ListDiscounts(DiscountQuery q, CancellationToken ct);
    Task<DiscountResponse?> GetDiscount(int id, CancellationToken ct);
    Task<ConfigurationResult<DiscountResponse>> UpdateDiscount(int id, DiscountRequest r, CancellationToken ct);
    Task<ConfigurationResult<DiscountResponse>> ActivateDiscount(int id, bool active, CancellationToken ct);
    Task<ConfigurationResult<ProductResponse>> CreateProduct(ProductRequest r, CancellationToken ct);
    Task<PagedResponse<ProductResponse>> ListProducts(ProductQuery q, CancellationToken ct);
    Task<ProductResponse?> GetProduct(int id, CancellationToken ct);
    Task<ConfigurationResult<ProductResponse>> UpdateProduct(int id, ProductRequest r, CancellationToken ct);
    Task<ConfigurationResult<ProductResponse>> ActivateProduct(int id, bool active, CancellationToken ct);
}

public sealed class ConfigurationService(AppDbContext db, TimeProvider clock) : IConfigurationService
{
    static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    static decimal Percentage(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    static string T(string v) => v.Trim();
    static string? Optional(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    async Task<bool> SaveUnique(Func<Task<bool>> duplicate, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await duplicate()) return false;
            throw;
        }
    }
    static PagedResponse<T> Page<T>(List<T> items, ConfigurationQuery q, int total) => new(items, q.Page, q.PageSize, total, (int)Math.Ceiling(total / (double)q.PageSize));

    public async Task<ConfigurationResult<BranchResponse>> CreateBranch(BranchRequest r, CancellationToken ct)
    {
        var code = T(r.Code);
        if (await db.Branches.AnyAsync(x => x.Code == code, ct)) return new(ConfigurationStatus.Conflict, null, "Branch code already exists.");
        var now = clock.GetUtcNow();
        var e = new Branch
        {
            Name = T(r.Name),
            Code = code,
            Address = T(r.Address),
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Add(e);
        if (!await SaveUnique(() => db.Branches.AsNoTracking().AnyAsync(x => x.Code == code, ct), ct)) return new(ConfigurationStatus.Conflict, null, "Branch code already exists.");
        return new(ConfigurationStatus.Success, B(e));
    }
    public async Task<PagedResponse<BranchResponse>> ListBranches(BranchQuery q, CancellationToken ct)
    {
        var s = db.Branches.AsNoTracking().AsQueryable();
        if (q.IsActive.HasValue) s = s.Where(x => x.IsActive == q.IsActive);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var v = T(q.Search);
            s = s.Where(x => x.Name.Contains(v) || x.Code.Contains(v) || x.Address.Contains(v));
        }
        var total = await s.CountAsync(ct);
        s = (q.SortBy.ToLowerInvariant(), q.SortDirection.ToLowerInvariant()) switch
        {
            ("name", "asc") => s.OrderBy(x => x.Name).ThenBy(x => x.Id),
            ("name", _) => s.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            ("code", "asc") => s.OrderBy(x => x.Code).ThenBy(x => x.Id),
            ("code", _) => s.OrderByDescending(x => x.Code).ThenBy(x => x.Id),
            ("createdat", "asc") => s.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            _ => s.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
        };
        return Page(await s.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).Select(x => new BranchResponse(x.Id, x.Name, x.Code, x.Address, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc)).ToListAsync(ct), q, total);
    }
    public Task<BranchResponse?> GetBranch(int id, CancellationToken ct) => db.Branches.AsNoTracking().Where(x => x.Id == id).Select(x => B(x)).SingleOrDefaultAsync(ct);
    public async Task<ConfigurationResult<BranchResponse>> UpdateBranch(int id, BranchRequest r, CancellationToken ct)
    {
        var e = await db.Branches.FindAsync([id], ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        var code = T(r.Code);
        if (await db.Branches.AnyAsync(x => x.Id != id && x.Code == code, ct)) return new(ConfigurationStatus.Conflict, null, "Branch code already exists.");
        e.Name = T(r.Name);
        e.Code = code;
        e.Address = T(r.Address);
        e.UpdatedAtUtc = clock.GetUtcNow();
        if (!await SaveUnique(() => db.Branches.AsNoTracking().AnyAsync(x => x.Id != id && x.Code == code, ct), ct)) return new(ConfigurationStatus.Conflict, null, "Branch code already exists.");
        return new(ConfigurationStatus.Success, B(e));
    }
    public async Task<ConfigurationResult<BranchResponse>> ActivateBranch(int id, bool active, CancellationToken ct)
    {
        var e = await db.Branches.FindAsync([id], ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        if (e.IsActive != active)
        {
            e.IsActive = active;
            e.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return new(ConfigurationStatus.Success, B(e));
    }

    public async Task<ConfigurationResult<RegisterResponse>> CreateRegister(CreateRegisterRequest r, CancellationToken ct)
    {
        var branch = await db.Branches.FindAsync([r.BranchId], ct);
        if (branch is null) return new(ConfigurationStatus.NotFound, null, "Branch not found.");
        if (!branch.IsActive) return new(ConfigurationStatus.Conflict, null, "The branch is inactive.");
        var code = T(r.Code);
        if (await db.Registers.AnyAsync(x => x.BranchId == r.BranchId && x.Code == code, ct)) return new(ConfigurationStatus.Conflict, null, "Register code already exists in this branch.");
        var now = clock.GetUtcNow();
        var e = new Register
        {
            BranchId = r.BranchId,
            Branch = branch,
            Name = T(r.Name),
            Code = code,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Add(e);
        if (!await SaveUnique(() => db.Registers.AsNoTracking().AnyAsync(x => x.BranchId == r.BranchId && x.Code == code, ct), ct)) return new(ConfigurationStatus.Conflict, null, "Register code already exists in this branch.");
        return new(ConfigurationStatus.Success, R(e));
    }
    public async Task<PagedResponse<RegisterResponse>> ListRegisters(RegisterQuery q, CancellationToken ct)
    {
        var s = db.Registers.AsNoTracking().AsQueryable();
        if (q.BranchId.HasValue) s = s.Where(x => x.BranchId == q.BranchId);
        if (q.IsActive.HasValue) s = s.Where(x => x.IsActive == q.IsActive);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var v = T(q.Search);
            s = s.Where(x => x.Name.Contains(v) || x.Code.Contains(v));
        }
        var total = await s.CountAsync(ct);
        s = Order(s, q.SortBy, q.SortDirection, x => x.Name, x => x.Code, x => x.CreatedAtUtc);
        var items = await s.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).Select(x => new RegisterResponse(x.Id, x.BranchId, x.Branch.Code, x.Branch.Name, x.Name, x.Code, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc)).ToListAsync(ct);
        return Page(items, q, total);
    }
    public Task<RegisterResponse?> GetRegister(int id, CancellationToken ct) => db.Registers.AsNoTracking().Where(x => x.Id == id).Select(x => new RegisterResponse(x.Id, x.BranchId, x.Branch.Code, x.Branch.Name, x.Name, x.Code, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc)).SingleOrDefaultAsync(ct);
    public async Task<ConfigurationResult<RegisterResponse>> UpdateRegister(int id, UpdateRegisterRequest r, CancellationToken ct)
    {
        var e = await db.Registers.Include(x => x.Branch).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        var code = T(r.Code);
        if (await db.Registers.AnyAsync(x => x.Id != id && x.BranchId == e.BranchId && x.Code == code, ct)) return new(ConfigurationStatus.Conflict, null, "Register code already exists in this branch.");
        e.Name = T(r.Name);
        e.Code = code;
        e.UpdatedAtUtc = clock.GetUtcNow();
        if (!await SaveUnique(() => db.Registers.AsNoTracking().AnyAsync(x => x.Id != id && x.BranchId == e.BranchId && x.Code == code, ct), ct)) return new(ConfigurationStatus.Conflict, null, "Register code already exists in this branch.");
        return new(ConfigurationStatus.Success, R(e));
    }
    public async Task<ConfigurationResult<RegisterResponse>> ActivateRegister(int id, bool active, CancellationToken ct)
    {
        var e = await db.Registers.Include(x => x.Branch).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        if (active && !e.Branch.IsActive) return new(ConfigurationStatus.Conflict, null, "A register under an inactive branch cannot be activated.");
        if (e.IsActive != active)
        {
            e.IsActive = active;
            e.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return new(ConfigurationStatus.Success, R(e));
    }

    public async Task<ConfigurationResult<TaxRateResponse>> CreateTaxRate(TaxRateRequest r, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var e = new TaxRate
        {
            Name = T(r.Name),
            Percentage = Percentage(r.Percentage),
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Add(e);
        await db.SaveChangesAsync(ct);
        return new(ConfigurationStatus.Success, X(e));
    }
    public async Task<PagedResponse<TaxRateResponse>> ListTaxRates(TaxRateQuery q, CancellationToken ct)
    {
        var s = db.TaxRates.AsNoTracking().AsQueryable();
        if (q.IsActive.HasValue) s = s.Where(x => x.IsActive == q.IsActive);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var v = T(q.Search);
            s = s.Where(x => x.Name.Contains(v));
        }
        var total = await s.CountAsync(ct);
        s = Order(s, q.SortBy, q.SortDirection, x => x.Name, x => x.Percentage, x => x.CreatedAtUtc, "percentage");
        return Page(await s.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).Select(x => X(x)).ToListAsync(ct), q, total);
    }
    public Task<TaxRateResponse?> GetTaxRate(int id, CancellationToken ct) => db.TaxRates.AsNoTracking().Where(x => x.Id == id).Select(x => X(x)).SingleOrDefaultAsync(ct);
    public async Task<ConfigurationResult<TaxRateResponse>> UpdateTaxRate(int id, TaxRateRequest r, CancellationToken ct)
    {
        var e = await db.TaxRates.FindAsync([id], ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        e.Name = T(r.Name);
        e.Percentage = Percentage(r.Percentage);
        e.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new(ConfigurationStatus.Success, X(e));
    }
    public async Task<ConfigurationResult<TaxRateResponse>> ActivateTaxRate(int id, bool active, CancellationToken ct)
    {
        var e = await db.TaxRates.FindAsync([id], ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        if (e.IsActive != active)
        {
            e.IsActive = active;
            e.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return new(ConfigurationStatus.Success, X(e));
    }

    public async Task<ConfigurationResult<DiscountResponse>> CreateDiscount(DiscountRequest r, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var e = new Discount
        {
            Name = T(r.Name),
            Type = r.Type,
            Value = r.Type == DiscountType.FixedAmount ? Money(r.Value) : Percentage(r.Value),
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Add(e);
        await db.SaveChangesAsync(ct);
        return new(ConfigurationStatus.Success, D(e));
    }
    public async Task<PagedResponse<DiscountResponse>> ListDiscounts(DiscountQuery q, CancellationToken ct)
    {
        var s = db.Discounts.AsNoTracking().AsQueryable();
        if (q.Type.HasValue) s = s.Where(x => x.Type == q.Type);
        if (q.IsActive.HasValue) s = s.Where(x => x.IsActive == q.IsActive);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var v = T(q.Search);
            s = s.Where(x => x.Name.Contains(v));
        }
        var total = await s.CountAsync(ct);
        s = Order(s, q.SortBy, q.SortDirection, x => x.Name, x => x.Value, x => x.CreatedAtUtc, "value");
        return Page(await s.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).Select(x => D(x)).ToListAsync(ct), q, total);
    }
    public Task<DiscountResponse?> GetDiscount(int id, CancellationToken ct) => db.Discounts.AsNoTracking().Where(x => x.Id == id).Select(x => D(x)).SingleOrDefaultAsync(ct);
    public async Task<ConfigurationResult<DiscountResponse>> UpdateDiscount(int id, DiscountRequest r, CancellationToken ct)
    {
        var e = await db.Discounts.FindAsync([id], ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        e.Name = T(r.Name);
        e.Type = r.Type;
        e.Value = r.Type == DiscountType.FixedAmount ? Money(r.Value) : Percentage(r.Value);
        e.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new(ConfigurationStatus.Success, D(e));
    }
    public async Task<ConfigurationResult<DiscountResponse>> ActivateDiscount(int id, bool active, CancellationToken ct)
    {
        var e = await db.Discounts.FindAsync([id], ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        if (e.IsActive != active)
        {
            e.IsActive = active;
            e.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return new(ConfigurationStatus.Success, D(e));
    }

    public async Task<ConfigurationResult<ProductResponse>> CreateProduct(ProductRequest r, CancellationToken ct)
    {
        var tax = await db.TaxRates.FindAsync([r.TaxRateId], ct);
        if (tax is null) return new(ConfigurationStatus.NotFound, null, "Tax rate not found.");
        if (!tax.IsActive) return new(ConfigurationStatus.Conflict, null, "The tax rate is inactive.");
        var sku = T(r.Sku);
        var barcode = Optional(r.Barcode);
        var conflict = await ProductConflict(0, sku, barcode, ct);
        if (conflict != null) return new(ConfigurationStatus.Conflict, null, conflict);
        var now = clock.GetUtcNow();
        var e = new Product
        {
            Sku = sku,
            Barcode = barcode,
            Name = T(r.Name),
            UnitPrice = Money(r.UnitPrice),
            TaxRateId = r.TaxRateId,
            TaxRate = tax,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Add(e);
        if (!await SaveUnique(async () => await ProductConflict(0, sku, barcode, ct) != null, ct)) return new(ConfigurationStatus.Conflict, null, await ProductConflict(0, sku, barcode, ct));
        return new(ConfigurationStatus.Success, P(e));
    }
    public async Task<PagedResponse<ProductResponse>> ListProducts(ProductQuery q, CancellationToken ct)
    {
        var s = db.Products.AsNoTracking().AsQueryable();
        if (q.TaxRateId.HasValue) s = s.Where(x => x.TaxRateId == q.TaxRateId);
        if (q.IsActive.HasValue) s = s.Where(x => x.IsActive == q.IsActive);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var v = T(q.Search);
            s = s.Where(x => x.Name.Contains(v) || x.Sku.Contains(v) || (x.Barcode != null && x.Barcode.Contains(v)));
        }
        var total = await s.CountAsync(ct);
        s = (q.SortBy.ToLowerInvariant(), q.SortDirection.ToLowerInvariant()) switch
        {
            ("name", "asc") => s.OrderBy(x => x.Name).ThenBy(x => x.Id),
            ("name", _) => s.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            ("sku", "asc") => s.OrderBy(x => x.Sku).ThenBy(x => x.Id),
            ("sku", _) => s.OrderByDescending(x => x.Sku).ThenBy(x => x.Id),
            ("unitprice", "asc") => s.OrderBy(x => x.UnitPrice).ThenBy(x => x.Id),
            ("unitprice", _) => s.OrderByDescending(x => x.UnitPrice).ThenBy(x => x.Id),
            ("createdat", "asc") => s.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            _ => s.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
        };
        var items = await s.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).Select(x => new ProductResponse(x.Id, x.Sku, x.Barcode, x.Name, x.UnitPrice, x.TaxRateId, x.TaxRate.Name, x.TaxRate.Percentage, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc)).ToListAsync(ct);
        return Page(items, q, total);
    }
    public Task<ProductResponse?> GetProduct(int id, CancellationToken ct) => db.Products.AsNoTracking().Where(x => x.Id == id).Select(x => new ProductResponse(x.Id, x.Sku, x.Barcode, x.Name, x.UnitPrice, x.TaxRateId, x.TaxRate.Name, x.TaxRate.Percentage, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc)).SingleOrDefaultAsync(ct);
    public async Task<ConfigurationResult<ProductResponse>> UpdateProduct(int id, ProductRequest r, CancellationToken ct)
    {
        var e = await db.Products.Include(x => x.TaxRate).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        var tax = await db.TaxRates.FindAsync([r.TaxRateId], ct);
        if (tax is null) return new(ConfigurationStatus.NotFound, null, "Tax rate not found.");
        if (!tax.IsActive) return new(ConfigurationStatus.Conflict, null, "The tax rate is inactive.");
        var sku = T(r.Sku);
        var barcode = Optional(r.Barcode);
        var conflict = await ProductConflict(id, sku, barcode, ct);
        if (conflict != null) return new(ConfigurationStatus.Conflict, null, conflict);
        e.Sku = sku;
        e.Barcode = barcode;
        e.Name = T(r.Name);
        e.UnitPrice = Money(r.UnitPrice);
        e.TaxRateId = r.TaxRateId;
        e.TaxRate = tax;
        e.UpdatedAtUtc = clock.GetUtcNow();
        if (!await SaveUnique(async () => await ProductConflict(id, sku, barcode, ct) != null, ct)) return new(ConfigurationStatus.Conflict, null, await ProductConflict(id, sku, barcode, ct));
        return new(ConfigurationStatus.Success, P(e));
    }
    public async Task<ConfigurationResult<ProductResponse>> ActivateProduct(int id, bool active, CancellationToken ct)
    {
        var e = await db.Products.Include(x => x.TaxRate).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return new(ConfigurationStatus.NotFound);
        if (active && !e.TaxRate.IsActive) return new(ConfigurationStatus.Conflict, null, "A product with an inactive tax rate cannot be activated.");
        if (e.IsActive != active)
        {
            e.IsActive = active;
            e.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return new(ConfigurationStatus.Success, P(e));
    }
    async Task<string?> ProductConflict(int id, string sku, string? barcode, CancellationToken ct)
    {
        if (await db.Products.AsNoTracking().AnyAsync(x => x.Id != id && x.Sku == sku, ct)) return "Product SKU already exists.";
        if (barcode != null && await db.Products.AsNoTracking().AnyAsync(x => x.Id != id && x.Barcode == barcode, ct)) return "Product barcode already exists.";
        return null;
    }

    static IQueryable<T> Order<T, TSecond>(IQueryable<T> s, string by, string dir, System.Linq.Expressions.Expression<Func<T, string>> name, System.Linq.Expressions.Expression<Func<T, TSecond>> second, System.Linq.Expressions.Expression<Func<T, DateTimeOffset>> created, string secondName = "code") where T : class => (by.ToLowerInvariant(), dir.ToLowerInvariant()) switch
    {
        ("name", "asc") => s.OrderBy(name).ThenBy(x => EF.Property<int>(x, "Id")),
        ("name", _) => s.OrderByDescending(name).ThenBy(x => EF.Property<int>(x, "Id")),
        var k when k.Item1 == secondName && k.Item2 == "asc" => s.OrderBy(second).ThenBy(x => EF.Property<int>(x, "Id")),
        var k when k.Item1 == secondName => s.OrderByDescending(second).ThenBy(x => EF.Property<int>(x, "Id")),
        ("createdat", "asc") => s.OrderBy(created).ThenBy(x => EF.Property<int>(x, "Id")),
        _ => s.OrderByDescending(created).ThenBy(x => EF.Property<int>(x, "Id"))
    };
    static BranchResponse B(Branch x) => new(x.Id, x.Name, x.Code, x.Address, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc);
    static RegisterResponse R(Register x) => new(x.Id, x.BranchId, x.Branch.Code, x.Branch.Name, x.Name, x.Code, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc);
    static TaxRateResponse X(TaxRate x) => new(x.Id, x.Name, x.Percentage, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc);
    static DiscountResponse D(Discount x) => new(x.Id, x.Name, x.Type, x.Value, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc);
    static ProductResponse P(Product x) => new(x.Id, x.Sku, x.Barcode, x.Name, x.UnitPrice, x.TaxRateId, x.TaxRate.Name, x.TaxRate.Percentage, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc);
}
