# RetailPOSApi

A production-oriented ASP.NET Core REST API foundation for a retail Point of Sale system.

## Technology

.NET 10, ASP.NET Core 10, Entity Framework Core 10, SQL Server, built-in OpenAPI, Scalar, and xUnit.

## Phase 01 status

Phase 01 establishes the solution, persistence model, initial migration, documentation, health endpoint, and automated foundation tests. Authentication and POS workflows are intentionally deferred to later phases.

## Local setup

```powershell
dotnet tool restore
dotnet restore
dotnet ef database update --project ./src/RetailPOSApi/RetailPOSApi.csproj --startup-project ./src/RetailPOSApi/RetailPOSApi.csproj
dotnet run --project ./src/RetailPOSApi/RetailPOSApi.csproj
```

The development connection uses Windows SQL Server LocalDB and contains no credentials.

- Health: `GET /health`
- OpenAPI JSON (Development): `/openapi/v1.json`
- Scalar UI (Development): `/scalar/v1`

Employee access, configuration CRUD, cashier shifts, sales, payments, refunds, and reporting are not implemented in Phase 01.
