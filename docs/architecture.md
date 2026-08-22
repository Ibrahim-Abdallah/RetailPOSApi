# Architecture

## Intent and structure

RetailPOSApi is deliberately a single ASP.NET Core Web API plus one test project. It favors focused domain services and explicit business workflows over generic repositories, CQRS, or extra class libraries.

```mermaid
flowchart LR
    Client --> Pipeline[ASP.NET Core pipeline]
    Pipeline --> Auth[JWT authentication and role authorization]
    Auth --> Controllers
    Controllers --> Services
    Services --> EF[EF Core transactional persistence]
    Services --> Dapper[Dapper reporting queries]
    EF --> SQL[(SQL Server)]
    Dapper --> SQL
```

Controllers translate HTTP input, FluentValidation results, and service outcomes into standard responses. Services own authentication and POS rules. `AppDbContext` and entity configurations own mappings, constraints, indexes, and transactions. EF Core is the system of record; Dapper is limited to parameterized sales and shift summary reads.

## Lifecycle and transaction boundaries

```mermaid
stateDiagram-v2
    [*] --> OpenShift
    OpenShift --> OpenSale
    OpenSale --> Completed: exact payments + commit
    Completed --> Voided: privileged and eligible
    Completed --> PartiallyRefunded: partial quantity
    Completed --> Refunded: full refund
    PartiallyRefunded --> PartiallyRefunded: another partial refund
    PartiallyRefunded --> Refunded: remaining quantity
    OpenShift --> ClosedShift: no open sales + reconcile
```

Sale completion, void/refund operations, and shift closing define explicit transaction boundaries. Database constraints and concurrency tokens back service pre-checks. Coordinators in integration tests force overlapping save windows so conflict paths are exercised deterministically.

```mermaid
sequenceDiagram
    participant C as Cashier client
    participant A as API/service
    participant D as Database
    C->>A: Complete sale + idempotency key + payments
    A->>D: Begin transaction / load open sale and shift
    A->>A: Revalidate lines and calculate totals
    A->>A: Validate exact payment match and cash change
    A->>D: Persist payments, receipt, Completed status
    D-->>A: Commit or concurrency conflict
    A-->>C: Completed receipt or safe conflict
```

## Time, money, and history

Time-dependent services use injected `TimeProvider`; persisted activity timestamps are UTC `DateTimeOffset` values. Monetary calculations use `decimal`, explicit two-place rounding, and `MidpointRounding.AwayFromZero`. Sale lines store names, prices, discounts, tax rates, per-unit calculations, and totals as historical snapshots. Refunds calculate from those snapshots, never the live catalog.

## Shift reconciliation

```mermaid
flowchart TD
    O[Opening float] --> E[Expected cash]
    S[Completed cash sales] --> E
    R[Cash refunds] -->|subtract| E
    V[Voided cash effects] -->|subtract| E
    E --> X[Variance = declared cash - expected cash]
    D[Cashier declared cash] --> X
    X --> C[Transactional shift close]
```

An open sale blocks closing. Expected cash and variance are server-calculated and stored as historical values.

## Authorization and API safety

JWT validation checks issuer, audience, signing key, lifetime, and roles with zero clock skew. Admin, Manager, and Cashier surfaces use role attributes; login, refresh, logout, and health are anonymous by design. Refresh tokens are cryptographically generated and stored only as hashes. OpenAPI and Scalar exist only in Development. Central exception handling logs method, path, trace ID, and the exception server-side while returning a generic Problem Details response with no exception detail.
