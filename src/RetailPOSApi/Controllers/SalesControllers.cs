using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Services;

namespace RetailPOSApi.Controllers;

[ApiController]
[Route("api/cashier/sales")]
[Authorize(Roles = nameof(UserRole.Cashier))]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class CashierSalesController(ISaleService service, ISaleCompletionService completionService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CancellationToken ct) => Result(await service.Create(ct), true);

    [HttpPost("{saleId:int}/complete")]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(int saleId, CompleteSaleRequest request,
        [FromServices] IValidator<CompleteSaleRequest> validator, CancellationToken ct) =>
        await ValidateAndRun(request, validator, () => completionService.Complete(saleId, request, ct), ct);

    [HttpGet]
    [ProducesResponseType<PagedResponse<SaleResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] SaleQuery query, [FromServices] IValidator<SaleQuery> validator, CancellationToken ct)
    {
        var invalid = await ValidateQuery(query, validator, ct);
        return invalid ?? Ok(await service.ListOwn(query, ct));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int id, CancellationToken ct) =>
        await service.GetOwn(id, ct) is { } sale ? Ok(sale) : ProblemResult(404, "Sale not found.");

    [HttpPost("{saleId:int}/lines")]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddLine(int saleId, AddSaleLineRequest request, [FromServices] IValidator<AddSaleLineRequest> validator, CancellationToken ct) =>
        await ValidateAndRun(request, validator, () => service.AddLine(saleId, request, ct), ct);

    [HttpPut("{saleId:int}/lines/{lineId:int}")]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateQuantity(int saleId, int lineId, UpdateSaleLineQuantityRequest request, [FromServices] IValidator<UpdateSaleLineQuantityRequest> validator, CancellationToken ct) =>
        await ValidateAndRun(request, validator, () => service.UpdateQuantity(saleId, lineId, request, ct), ct);

    [HttpPut("{saleId:int}/lines/{lineId:int}/discount")]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ApplyDiscount(int saleId, int lineId, ApplySaleLineDiscountRequest request, [FromServices] IValidator<ApplySaleLineDiscountRequest> validator, CancellationToken ct) =>
        await ValidateAndRun(request, validator, () => service.ApplyDiscount(saleId, lineId, request, ct), ct);

    [HttpDelete("{saleId:int}/lines/{lineId:int}/discount")]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveDiscount(int saleId, int lineId, CancellationToken ct) => Result(await service.RemoveDiscount(saleId, lineId, ct));

    [HttpDelete("{saleId:int}/lines/{lineId:int}")]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveLine(int saleId, int lineId, CancellationToken ct) => Result(await service.RemoveLine(saleId, lineId, ct));

    async Task<IActionResult> ValidateAndRun<T>(T request, IValidator<T> validator, Func<Task<SaleOperationResult>> run, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        return validation.IsValid ? Result(await run()) : ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
    }

    async Task<IActionResult?> ValidateQuery<T>(T query, IValidator<T> validator, CancellationToken ct)
    {
        if (Request.Query.Any(x => (x.Key.Equals("sortBy", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("sortDirection", StringComparison.OrdinalIgnoreCase)) && string.IsNullOrWhiteSpace(x.Value)))
        {
            ModelState.AddModelError("sort", "Sort field and direction must not be empty.");
            return ValidationProblem(ModelState);
        }
        var validation = await validator.ValidateAsync(query, ct);
        return validation.IsValid ? null : ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
    }

    IActionResult Result(SaleOperationResult result, bool created = false) => result.Status switch
    {
        SaleOperationStatus.Success when created => CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value),
        SaleOperationStatus.Success => Ok(result.Value),
        SaleOperationStatus.BadRequest => ProblemResult(400, result.Message!),
        SaleOperationStatus.NotFound => ProblemResult(404, result.Message!),
        SaleOperationStatus.Conflict => ProblemResult(409, result.Message!),
        _ => ProblemResult(403, result.Message!)
    };
    ObjectResult ProblemResult(int status, string title) => StatusCode(status, new ProblemDetails { Status = status, Title = title });
}

[ApiController]
[Route("api/management/sales")]
[Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.Manager)}")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class ManagementSalesController(ISaleService service, IValidator<ManagementSaleQuery> validator) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResponse<SaleResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ManagementSaleQuery query, CancellationToken ct)
    {
        if (Request.Query.Any(x => (x.Key.Equals("sortBy", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("sortDirection", StringComparison.OrdinalIgnoreCase)) && string.IsNullOrWhiteSpace(x.Value)))
        {
            ModelState.AddModelError("sort", "Sort field and direction must not be empty.");
            return ValidationProblem(ModelState);
        }
        var validation = await validator.ValidateAsync(query, ct);
        return validation.IsValid ? Ok(await service.ListManagement(query, ct)) : ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<SaleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int id, CancellationToken ct) =>
        await service.GetManagement(id, ct) is { } sale ? Ok(sale) : NotFound(new ProblemDetails { Status = 404, Title = "Sale not found." });
}
