using Microsoft.EntityFrameworkCore;
using RetailPOSApi.Persistence;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }))
    .WithName("Health")
    .WithTags("Health");

app.Run();

public partial class Program;
