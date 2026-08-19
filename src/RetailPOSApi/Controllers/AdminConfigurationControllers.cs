using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.Services;

namespace RetailPOSApi.Controllers;

[ApiController]
[Authorize(Roles = nameof(UserRole.Admin))]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public abstract class AdminConfigurationController : ControllerBase
{
    protected IActionResult Result<T>(ConfigurationResult<T> r) => r.Status switch
    {
        ConfigurationStatus.Success => Ok(r.Value),
        ConfigurationStatus.NotFound => NotFound(new ProblemDetails
        {
            Status = 404,
            Title = r.Message ?? "Resource not found."
        }),
        _ => Conflict(new ProblemDetails
        {
            Status = 409,
            Title = r.Message ?? "The request conflicts with the current state."
        })
    };
    protected IActionResult Invalid(FluentValidation.Results.ValidationResult r) => ValidationProblem(new ValidationProblemDetails(r.ToDictionary()));
    protected IActionResult Missing(string title) => NotFound(new ProblemDetails
    {
        Status = 404,
        Title = title
    });
}

[Route("api/admin/branches")]
public sealed class AdminBranchesController(IConfigurationService service, IValidator<BranchRequest> validator, IValidator<BranchQuery> queryValidator) : AdminConfigurationController
{
    [HttpGet]
    [ProducesResponseType<PagedResponse<BranchResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] BranchQuery q, CancellationToken ct)
    {
        var v = await queryValidator.ValidateAsync(q, ct);
        return v.IsValid ? Ok(await service.ListBranches(q, ct)) : Invalid(v);
    }
    [HttpGet("{id:int}")]
    [ProducesResponseType<BranchResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var v = await service.GetBranch(id, ct);
        return v is null ? Missing("Branch not found.") : Ok(v);
    }
    [HttpPost]
    public async Task<IActionResult> Create(BranchRequest r, CancellationToken ct)
    {
        var v = await validator.ValidateAsync(r, ct);
        if (!v.IsValid) return Invalid(v);
        var x = await service.CreateBranch(r, ct);
        return x.Status == ConfigurationStatus.Success ? CreatedAtAction(nameof(Get), new
        {
            id = x.Value!.Id
        }, x.Value) : Result(x);
    }
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, BranchRequest r, CancellationToken ct)
    {
        var v = await validator.ValidateAsync(r, ct);
        return v.IsValid ? Result(await service.UpdateBranch(id, r, ct)) : Invalid(v);
    }
    [HttpPatch("{id:int}/activation")] public async Task<IActionResult> Activate(int id, ActivationRequest r, CancellationToken ct) => Result(await service.ActivateBranch(id, r.IsActive, ct));
}
[Route("api/admin/registers")]
public sealed class AdminRegistersController(IConfigurationService service, IValidator<CreateRegisterRequest> createValidator, IValidator<UpdateRegisterRequest> updateValidator, IValidator<RegisterQuery> queryValidator) : AdminConfigurationController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] RegisterQuery q, CancellationToken ct)
    {
        var v = await queryValidator.ValidateAsync(q, ct);
        return v.IsValid ? Ok(await service.ListRegisters(q, ct)) : Invalid(v);
    }
    [HttpGet("{id:int}")]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var v = await service.GetRegister(id, ct);
        return v is null ? Missing("Register not found.") : Ok(v);
    }
    [HttpPost]
    public async Task<IActionResult> Create(CreateRegisterRequest r, CancellationToken ct)
    {
        var v = await createValidator.ValidateAsync(r, ct);
        if (!v.IsValid) return Invalid(v);
        var x = await service.CreateRegister(r, ct);
        return x.Status == ConfigurationStatus.Success ? CreatedAtAction(nameof(Get), new
        {
            id = x.Value!.Id
        }, x.Value) : Result(x);
    }
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateRegisterRequest r, CancellationToken ct)
    {
        var v = await updateValidator.ValidateAsync(r, ct);
        return v.IsValid ? Result(await service.UpdateRegister(id, r, ct)) : Invalid(v);
    }
    [HttpPatch("{id:int}/activation")] public async Task<IActionResult> Activate(int id, ActivationRequest r, CancellationToken ct) => Result(await service.ActivateRegister(id, r.IsActive, ct));
}
[Route("api/admin/tax-rates")]
public sealed class AdminTaxRatesController(IConfigurationService service, IValidator<TaxRateRequest> validator, IValidator<TaxRateQuery> queryValidator) : AdminConfigurationController
{
    [HttpGet]
    [ProducesResponseType<PagedResponse<TaxRateResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] TaxRateQuery q, CancellationToken ct)
    {
        var v = await queryValidator.ValidateAsync(q, ct);
        return v.IsValid ? Ok(await service.ListTaxRates(q, ct)) : Invalid(v);
    }
    [HttpGet("{id:int}")]
    [ProducesResponseType<TaxRateResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var v = await service.GetTaxRate(id, ct);
        return v is null ? Missing("Tax rate not found.") : Ok(v);
    }
    [HttpPost]
    public async Task<IActionResult> Create(TaxRateRequest r, CancellationToken ct)
    {
        var v = await validator.ValidateAsync(r, ct);
        if (!v.IsValid) return Invalid(v);
        var x = await service.CreateTaxRate(r, ct);
        return CreatedAtAction(nameof(Get), new
        {
            id = x.Value!.Id
        }, x.Value);
    }
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, TaxRateRequest r, CancellationToken ct)
    {
        var v = await validator.ValidateAsync(r, ct);
        return v.IsValid ? Result(await service.UpdateTaxRate(id, r, ct)) : Invalid(v);
    }
    [HttpPatch("{id:int}/activation")] public async Task<IActionResult> Activate(int id, ActivationRequest r, CancellationToken ct) => Result(await service.ActivateTaxRate(id, r.IsActive, ct));
}
[Route("api/admin/discounts")]
public sealed class AdminDiscountsController(IConfigurationService service, IValidator<DiscountRequest> validator, IValidator<DiscountQuery> queryValidator) : AdminConfigurationController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DiscountQuery q, CancellationToken ct)
    {
        var v = await queryValidator.ValidateAsync(q, ct);
        return v.IsValid ? Ok(await service.ListDiscounts(q, ct)) : Invalid(v);
    }
    [HttpGet("{id:int}")]
    [ProducesResponseType<DiscountResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var v = await service.GetDiscount(id, ct);
        return v is null ? Missing("Discount not found.") : Ok(v);
    }
    [HttpPost]
    public async Task<IActionResult> Create(DiscountRequest r, CancellationToken ct)
    {
        var v = await validator.ValidateAsync(r, ct);
        if (!v.IsValid) return Invalid(v);
        var x = await service.CreateDiscount(r, ct);
        return CreatedAtAction(nameof(Get), new
        {
            id = x.Value!.Id
        }, x.Value);
    }
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, DiscountRequest r, CancellationToken ct)
    {
        var v = await validator.ValidateAsync(r, ct);
        return v.IsValid ? Result(await service.UpdateDiscount(id, r, ct)) : Invalid(v);
    }
    [HttpPatch("{id:int}/activation")] public async Task<IActionResult> Activate(int id, ActivationRequest r, CancellationToken ct) => Result(await service.ActivateDiscount(id, r.IsActive, ct));
}
[Route("api/admin/products")]
public sealed class AdminProductsController(IConfigurationService service, IValidator<ProductRequest> validator, IValidator<ProductQuery> queryValidator) : AdminConfigurationController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ProductQuery q, CancellationToken ct)
    {
        var v = await queryValidator.ValidateAsync(q, ct);
        return v.IsValid ? Ok(await service.ListProducts(q, ct)) : Invalid(v);
    }
    [HttpGet("{id:int}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var v = await service.GetProduct(id, ct);
        return v is null ? Missing("Product not found.") : Ok(v);
    }
    [HttpPost]
    public async Task<IActionResult> Create(ProductRequest r, CancellationToken ct)
    {
        var v = await validator.ValidateAsync(r, ct);
        if (!v.IsValid) return Invalid(v);
        var x = await service.CreateProduct(r, ct);
        return x.Status == ConfigurationStatus.Success ? CreatedAtAction(nameof(Get), new
        {
            id = x.Value!.Id
        }, x.Value) : Result(x);
    }
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, ProductRequest r, CancellationToken ct)
    {
        var v = await validator.ValidateAsync(r, ct);
        return v.IsValid ? Result(await service.UpdateProduct(id, r, ct)) : Invalid(v);
    }
    [HttpPatch("{id:int}/activation")] public async Task<IActionResult> Activate(int id, ActivationRequest r, CancellationToken ct) => Result(await service.ActivateProduct(id, r.IsActive, ct));
}
