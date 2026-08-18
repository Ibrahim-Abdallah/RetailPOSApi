using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Employees;
using RetailPOSApi.Services;

namespace RetailPOSApi.Controllers;

[ApiController, Authorize(Roles = nameof(UserRole.Admin)), Route("api/admin/employees")]
public sealed class AdminEmployeesController(IEmployeeService employees, IValidator<CreateEmployeeRequest> createValidator, IValidator<EmployeeQuery> queryValidator) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<EmployeeSummary>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var validation = await createValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid) return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        var result = await employees.CreateAsync(request, cancellationToken);
        if (result.Status == CreateEmployeeStatus.Duplicate) return Conflict(new ProblemDetails { Status = 409, Title = "An employee with this email already exists." });
        return CreatedAtAction(nameof(Get), new { id = result.Employee!.Id }, result.Employee);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] EmployeeQuery query, CancellationToken cancellationToken)
    {
        var validation = await queryValidator.ValidateAsync(query, cancellationToken);
        return validation.IsValid ? Ok(await employees.ListAsync(query, cancellationToken)) : ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var employee = await employees.GetAsync(id, cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    [HttpPatch("{id:int}/activation")]
    public async Task<IActionResult> SetActivation(int id, ActivationRequest request, CancellationToken cancellationToken)
    {
        var employee = await employees.SetActivationAsync(id, request.IsActive, cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }
}
