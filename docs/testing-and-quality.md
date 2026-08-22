# Testing and quality

## Automated strategy

The xUnit suite combines focused service tests and end-to-end HTTP tests through `WebApplicationFactory`. Integration tests replace production SQL Server registration with a temporary SQLite database configured for relational constraints and transactions. EF InMemory is avoided because it does not faithfully exercise relational uniqueness, foreign keys, transactions, or SQL translation.

Test-only save coordinators synchronize competing shift and sale mutations, making concurrency outcomes repeatable. Coverage includes authentication and refresh replay, role authorization, configuration, calculations and snapshots, completion/idempotency, refund and close transactions, reconciliation, Dapper results, Problem Details, OpenAPI security metadata, and Scalar reachability.

## Quality and security checks

- `dotnet build` and the full `dotnet test` suite must be warning-free and passing.
- Both projects are scanned with `dotnet list ... package --vulnerable --include-transitive`.
- `dotnet ef migrations has-pending-model-changes` guards against an accidental model change.
- Unexpected exceptions produce generic `application/problem+json`; tests assert that sensitive messages, types, paths, and stack details are absent.
- 401, 403, unknown-route 404, validation 400, business 404, and conflict 409 retain their status semantics.
- JWT signing keys and bootstrap credentials are blank in committed configuration. Refresh-token hashes and password hashes are never serialized.
- Logging must not add Authorization headers, access/refresh tokens, passwords, credential-bearing bodies, connection strings, or raw sensitive payment data.

## Environment limits

SQLite is an efficient relational CI baseline but does not prove every SQL Server locking, filtered-index, or provider-specific Dapper behavior. Those areas require manual SQL Server verification. CI intentionally has no database service, deployment credentials, production observability, payment gateway, or infrastructure assurance. Scalar and OpenAPI are manually checked in Development before screenshots are captured.
