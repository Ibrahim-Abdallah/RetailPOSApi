using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.DTOs.Shifts;
using RetailPOSApi.Services;

namespace RetailPOSApi.Controllers;

[ApiController]
[Route("api/cashier/shifts")]
[Authorize(Roles = nameof(UserRole.Cashier))]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class CashierShiftsController(ICashierShiftService service, IValidator<OpenShiftRequest> openValidator,
    IValidator<CloseShiftRequest> closeValidator,
    IValidator<ShiftQuery> queryValidator) : ControllerBase
{
    [HttpPost("open")]
    [ProducesResponseType<ShiftResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Open(OpenShiftRequest request, CancellationToken ct)
    {
        var validation = await openValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        var result = await service.Open(request, ct);
        return result.Status switch
        {
            ShiftOperationStatus.Success => CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value),
            ShiftOperationStatus.NotFound => ProblemResult(404, result.Message!),
            ShiftOperationStatus.Conflict => ProblemResult(409, result.Message!),
            _ => ProblemResult(403, result.Message!)
        };
    }

    [HttpPost("{id:int}/close")]
    [ProducesResponseType<ShiftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Close(int id, CloseShiftRequest request, CancellationToken ct)
    {
        var validation = await closeValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        var result = await service.Close(id, request, ct);
        return result.Status switch
        {
            ShiftOperationStatus.Success => Ok(result.Value),
            ShiftOperationStatus.NotFound => ProblemResult(404, result.Message!),
            ShiftOperationStatus.Conflict => ProblemResult(409, result.Message!),
            _ => ProblemResult(403, result.Message!)
        };
    }

    [HttpGet("current")]
    [ProducesResponseType<ShiftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var shift = await service.Current(ct);
        return shift is null ? ProblemResult(404, "No open cashier shift found.") : Ok(shift);
    }

    [HttpGet]
    [ProducesResponseType<PagedResponse<ShiftResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ShiftQuery query, CancellationToken ct)
    {
        if (HasEmptySortValue()) return EmptySortValidationProblem();
        var validation = await queryValidator.ValidateAsync(query, ct);
        return validation.IsValid ? Ok(await service.ListOwn(query, ct)) : ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<ShiftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var shift = await service.GetOwn(id, ct);
        return shift is null ? ProblemResult(404, "Cashier shift not found.") : Ok(shift);
    }

    ObjectResult ProblemResult(int status, string title) => StatusCode(status, new ProblemDetails { Status = status, Title = title });
    bool HasEmptySortValue() => Request.Query.Any(x =>
        (x.Key.Equals("sortBy", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("sortDirection", StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrWhiteSpace(x.Value.ToString()));
    IActionResult EmptySortValidationProblem()
    {
        ModelState.AddModelError("sort", "Sort field and direction must not be empty.");
        return ValidationProblem(ModelState);
    }
}

[ApiController]
[Route("api/management/shifts")]
[Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.Manager)}")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class ManagementShiftsController(ICashierShiftService service, IValidator<ManagementShiftQuery> queryValidator) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResponse<ShiftResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ManagementShiftQuery query, CancellationToken ct)
    {
        if (Request.Query.Any(x =>
                (x.Key.Equals("sortBy", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("sortDirection", StringComparison.OrdinalIgnoreCase)) &&
                string.IsNullOrWhiteSpace(x.Value.ToString())))
        {
            ModelState.AddModelError("sort", "Sort field and direction must not be empty.");
            return ValidationProblem(ModelState);
        }
        var validation = await queryValidator.ValidateAsync(query, ct);
        return validation.IsValid ? Ok(await service.ListManagement(query, ct)) : ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<ShiftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var shift = await service.GetManagement(id, ct);
        return shift is null ? NotFound(new ProblemDetails { Status = 404, Title = "Cashier shift not found." }) : Ok(shift);
    }
}
