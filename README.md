# RetailPOSApi

A production-oriented ASP.NET Core REST API foundation for a retail Point of Sale system.

## Technology

.NET 10, ASP.NET Core 10, Entity Framework Core 10, SQL Server, built-in OpenAPI, Scalar, and xUnit.

## Phase 02 status

Phase 02 adds JWT employee authentication and Admin-managed employee access while preserving the Phase 01 persistence guarantees. Refresh tokens and POS workflows remain deferred.

## Local setup

```powershell
dotnet tool restore
dotnet restore
dotnet user-secrets --project ./src/RetailPOSApi/RetailPOSApi.csproj set "Jwt:SigningKey" "<at-least-32-character-development-secret>"
dotnet ef database update --project ./src/RetailPOSApi/RetailPOSApi.csproj --startup-project ./src/RetailPOSApi/RetailPOSApi.csproj
dotnet run --project ./src/RetailPOSApi/RetailPOSApi.csproj
```

The development connection uses Windows SQL Server LocalDB and contains no credentials.

`Jwt:Issuer`, `Jwt:Audience`, a signing key of at least 32 characters, and an access-token lifetime from 1 to 1440 minutes are validated at startup. The signing key must come from User Secrets or environment variables.

The first Admin can be created without a public registration endpoint by setting `BootstrapAdmin:Enabled`, `FirstName`, `LastName`, `Email`, and `Password` through User Secrets/environment variables. Bootstrap is disabled by default, hashes the password, and is idempotent; disable it again after provisioning.

- Health: `GET /health`
- OpenAPI JSON (Development): `/openapi/v1.json`
- Scalar UI (Development): `/scalar/v1`
- Login: `POST /api/auth/login`
- Admin employees: `POST/GET /api/admin/employees`, `GET /api/admin/employees/{id}`, `PATCH /api/admin/employees/{id}/activation`

In Scalar, enter the JWT under the Bearer security scheme. OpenAPI and Scalar remain Development-only. Refresh tokens, configuration CRUD, cashier shifts, sales, payments, refunds, and reporting are not implemented in Phase 02.
