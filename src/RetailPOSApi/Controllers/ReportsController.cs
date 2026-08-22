using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Reports;
using RetailPOSApi.Services;

namespace RetailPOSApi.Controllers;

[ApiController]
[Route("api/management/reports")]
[Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.Manager)}")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class ReportsController(IReportingService reporting, IValidator<ReportQuery> validator) : ControllerBase
{
    [HttpGet("sales-summary")]
    [ProducesResponseType<SalesSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SalesSummary([FromQuery] ReportQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);
        return validation.IsValid
            ? Ok(await reporting.GetSalesSummary(query, cancellationToken))
            : ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
    }

    [HttpGet("shift-summary")]
    [ProducesResponseType<ShiftSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ShiftSummary([FromQuery] ReportQuery query, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);
        return validation.IsValid
            ? Ok(await reporting.GetShiftSummary(query, cancellationToken))
            : ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
    }
}
