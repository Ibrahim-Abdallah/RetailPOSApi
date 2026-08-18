using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.Services;

namespace RetailPOSApi.Controllers;

[ApiController, Route("api/auth")]
public sealed class AuthController(IAuthenticationService authentication, IValidator<LoginRequest> validator) : ControllerBase
{
    [AllowAnonymous, HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid) return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        var result = await authentication.LoginAsync(request, cancellationToken);
        return result is null ? Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid credentials." }) : Ok(result);
    }
}
