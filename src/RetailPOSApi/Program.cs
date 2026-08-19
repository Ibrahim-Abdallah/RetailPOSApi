using System.Security.Claims;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using RetailPOSApi.Configuration;
using RetailPOSApi.Domain;
using RetailPOSApi.Persistence;
using RetailPOSApi.Services;
using RetailPOSApi.Validation;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Enter a JWT access token."
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any() || !metadata.OfType<IAuthorizeData>().Any())
            return Task.CompletedTask;

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
        });
        return Task.CompletedTask;
    });
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddSingleton<IRefreshAttemptCoordinator, RefreshAttemptCoordinator>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<IEmployeeService, EmployeeService>();
builder.Services.AddScoped<IConfigurationService, ConfigurationService>();
builder.Services.AddScoped<ICashierShiftService, CashierShiftService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<AdminBootstrapService>();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "Jwt:Issuer must not be blank.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Audience), "Jwt:Audience must not be blank.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.SigningKey), "Jwt:SigningKey must not be blank.")
    .ValidateOnStart();
builder.Services.Configure<BootstrapAdminOptions>(builder.Configuration.GetSection(BootstrapAdminOptions.SectionName));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<JwtOptions>>((options, configured) =>
{
    var jwt = configured.Value;
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
        NameClaimType = ClaimTypes.NameIdentifier, RoleClaimType = ClaimTypes.Role
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.AddPreferredSecuritySchemes("Bearer"));
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous().WithName("Health").WithTags("Health");

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<AdminBootstrapService>().RunAsync(CancellationToken.None);

app.Run();

public partial class Program;
