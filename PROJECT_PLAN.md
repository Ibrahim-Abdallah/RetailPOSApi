# Retail POS API — Project Plan

## 1. Project Purpose

Build a portfolio-ready ASP.NET Core REST API for a realistic retail Point of Sale system.

The project should demonstrate backend engineering beyond basic CRUD through a complete retail sales workflow:

Branch
→ Register
→ Cashier Shift
→ Sale
→ Payment
→ Receipt
→ Void / Refund
→ Shift Closing
→ Reporting

The project should showcase:

- ASP.NET Core REST API design
- JWT authentication
- Refresh-token rotation and revocation
- Role-based authorization
- Entity Framework Core
- SQL Server
- Database transactions
- Concurrency protection
- Cashier shift management
- Transactional retail sales
- Historical price, tax, and discount snapshots
- Deterministic monetary calculations
- Multiple payment methods
- Cash tender and change calculation
- Receipt generation
- Sale voiding
- Partial and full refunds
- Cash reconciliation
- Pagination, filtering, and sorting
- FluentValidation
- Problem Details
- Dapper reporting
- Automated unit and integration tests
- OpenAPI / Scalar documentation
- Professional GitHub portfolio presentation

The goal is not to build a complete ERP, accounting package, warehouse management system, payment gateway, or frontend POS application.

The project must remain focused on backend business rules commonly found in retail POS systems.

The primary technical differentiator is:

Cashier shift control

- Transactional sale completion
- Deterministic tax/discount calculations
- Payment handling
- Refund safety
- Cash reconciliation

  ***

# 2. Project Success Criteria

The finished repository should quickly demonstrate that the developer can implement business-rule-heavy backend systems rather than simple CRUD APIs.

A reviewer should be able to identify:

- Secure authentication
- Employee role management
- Multi-branch/register modeling
- Transactional POS sales
- Monetary calculation correctness
- Historical financial snapshots
- Payment processing abstractions
- Refund and void rules
- Database concurrency protection
- Cashier shift reconciliation
- SQL reporting with Dapper
- Automated testing
- Professional API documentation

Keep the architecture intentionally understandable.

Avoid unnecessary enterprise architecture ceremony.

---

# 3. Technology Stack

Use:

- .NET 10
- ASP.NET Core 10 Web API
- C#
- Entity Framework Core 10
- SQL Server
- JWT Bearer Authentication
- ASP.NET Core built-in OpenAPI
- Scalar
- FluentValidation
- Dapper
- xUnit
- ASP.NET Core integration testing

Use framework-native functionality where practical.

Do not introduce:

- MediatR
- CQRS
- RabbitMQ
- Redis
- Kafka
- Hangfire
- Quartz
- Kubernetes
- Microservices

unless a concrete requirement appears later.

---

# 4. Repository Structure

Repository:

RetailPOSApi

Target structure:

RetailPOSApi/
├── src/
│ └── RetailPOSApi/
├── tests/
│ └── RetailPOSApi.Tests/
├── docs/
├── screenshots/
├── README.md
├── PROJECT_PLAN.md
├── .gitignore
├── global.json
└── RetailPOSApi.slnx

Keep the solution as a single application project plus a test project.

Do not introduce multiple class-library layers without a concrete reason.

---

# 5. Application Areas

The application contains the following main areas:

1. Authentication
2. Employee Management
3. Branches
4. Registers
5. Product Catalog
6. Tax Rates
7. Discounts
8. Cashier Shifts
9. Sales
10. Payments
11. Receipts
12. Voids
13. Refunds
14. Shift Closing
15. Reporting

---

# 6. Roles

Implement three roles:

Admin
Manager
Cashier

## Admin

Can:

- Manage employees
- Manage branches
- Manage registers
- Manage products
- Manage tax rates
- Manage discounts
- View all sales
- View all shifts
- Void sales
- Process refunds
- View reports

## Manager

Can:

- View operational sales
- View shifts
- Void eligible sales
- Process refunds
- View operational reports

## Cashier

Can:

- Open a shift
- Operate the assigned register
- Build sales
- Complete sales
- Accept payments
- View own sales
- Close own shift

There is no public user registration.

Employee accounts are provisioned through trusted Admin functionality.

A bootstrap Admin may be configured safely for development.

---

# 7. Time Model

Persist timestamps in UTC.

Use DateTimeOffset where practical.

Never rely on the server's local clock for business logic.

Register:

TimeProvider.System

and inject TimeProvider into time-dependent services.

Time-dependent tests should use a controllable TimeProvider.

---

# 8. Monetary Rules

Financial correctness is one of the project's most important requirements.

Use decimal values for money.

Suggested SQL precision:

decimal(18,2)

Percentage values should use sufficient decimal precision.

All monetary rounding must use one centralized rule:

MidpointRounding.AwayFromZero

Round monetary values to two decimal places at explicitly defined calculation boundaries.

Never use double or float for financial calculations.

---

# 9. Core Entities

Core entities:

User
RefreshToken
Branch
Register
CashierShift
Product
TaxRate
Discount
Sale
SaleLine
Payment
Refund
RefundLine
RefundPayment

Supporting enums may include:

UserRole
RegisterStatus
CashierShiftStatus
SaleStatus
PaymentMethod
PaymentStatus
DiscountType
RefundStatus

---

# 10. Branch

Suggested fields:

Id
Name
Code
Address
IsActive
CreatedAtUtc
UpdatedAtUtc

Branch Code must be unique.

Inactive branches cannot start new cashier shifts.

Historical sales remain attached to inactive branches.

---

# 11. Register

Each Register belongs to one Branch.

Suggested fields:

Id
BranchId
Name
Code
IsActive
CreatedAtUtc
UpdatedAtUtc

Register Code should be unique within a Branch.

Inactive Registers cannot open new shifts.

Historical shifts and sales remain preserved.

---

# 12. Product

The POS Product catalog is intentionally smaller than an inventory-management system.

Suggested fields:

Id
Sku
Barcode
Name
UnitPrice
TaxRateId
IsActive
CreatedAtUtc
UpdatedAtUtc

Rules:

- SKU must be unique.
- Barcode is optional but unique when provided.
- UnitPrice must be non-negative.
- Products use configured Tax Rates.
- Inactive Products cannot be added to new sales.

Version 1 does not manage warehouse inventory or stock quantities.

Inventory management belongs to a separate portfolio project.

---

# 13. Tax Rate

Suggested fields:

Id
Name
Percentage
IsActive
CreatedAtUtc
UpdatedAtUtc

Rules:

- Percentage must be between 0 and 100.
- Historical SaleLines must not depend on the current TaxRate value.
- Tax percentage is snapshotted when the SaleLine is created.

---

# 14. Discount

Version 1 supports configured line-level discounts.

Suggested fields:

Id
Name
Type
Value
IsActive
CreatedAtUtc
UpdatedAtUtc

DiscountType:

Percentage
FixedAmount

Rules:

- One optional Discount per SaleLine.
- Discounts are not stackable in Version 1.
- Percentage discounts must be between 0 and 100.
- Fixed discounts are interpreted per unit.
- A discount may never reduce a unit below zero.
- Discount values are snapshotted into SaleLine.
- Later Discount changes do not alter historical sales.

---

# 15. Cashier Shift

A Cashier must have an open shift before creating sales.

Suggested fields:

Id
BranchId
RegisterId
CashierUserId
Status
OpeningFloat
OpenedAtUtc
ClosedAtUtc
DeclaredCash
ExpectedCash
CashVariance
CreatedAtUtc
UpdatedAtUtc

Statuses:

Open
Closed

Rules:

- One open shift per Register.
- One open shift per Cashier.
- OpeningFloat must be non-negative.
- Sales can only be created against an Open shift.
- Shift Branch and Register must match.
- Closed shifts cannot accept new Sales.
- Closing values become historical financial records.

Database-level protection must back critical open-shift uniqueness rules.

Do not rely only on application-level pre-checks.

---

# 16. Sale

Suggested fields:

Id
ReceiptNumber
BranchId
RegisterId
CashierShiftId
CashierUserId
Status
Subtotal
DiscountTotal
TaxTotal
TotalAmount
CompletedAtUtc
VoidedAtUtc
VoidedByUserId
VoidReason
CreatedAtUtc
UpdatedAtUtc
RowVersion

Statuses:

Open
Completed
Voided
PartiallyRefunded
Refunded

ReceiptNumber must be unique.

Sale ownership and register context are derived server-side from the authenticated Cashier and active shift.

Clients cannot submit authoritative:

- CashierUserId
- BranchId
- RegisterId
- SaleStatus
- totals
- tax totals
- discount totals
- receipt numbers

---

# 17. Sale Line

Suggested historical snapshot fields:

Id
SaleId
ProductId
ProductSku
ProductName
Quantity
UnitPrice
DiscountId
DiscountName
DiscountType
DiscountValue
UnitDiscountAmount
UnitNetAmount
TaxRateId
TaxRateName
TaxRatePercentage
UnitTaxAmount
UnitTotal
LineSubtotal
LineDiscountTotal
LineTaxTotal
LineTotal

The SaleLine must preserve historical financial information.

Later changes to:

- Product name
- Product price
- Tax Rate
- Discount

must never alter completed Sale history.

---

# 18. Sale Calculation

For each unit:

UnitPrice

- UnitDiscountAmount
  = UnitNetAmount

UnitNetAmount
× TaxRate
= UnitTaxAmount

UnitNetAmount

- UnitTaxAmount
  = UnitTotal

Then:

LineSubtotal = UnitPrice × Quantity

LineDiscountTotal = UnitDiscountAmount × Quantity

LineTaxTotal = UnitTaxAmount × Quantity

LineTotal = UnitTotal × Quantity

Sale totals are the sum of SaleLine totals.

All values are calculated server-side.

---

# 19. Open Sale Workflow

A Cashier with an active Shift may create an Open Sale.

Workflow:

Open Shift
↓
Create Sale
↓
Add Products
↓
Update Quantities
↓
Apply / Remove Discount
↓
Recalculate Totals
↓
Complete Payment
↓
Completed Sale + Receipt

Open Sales may be modified.

Completed, Voided, PartiallyRefunded, and Refunded Sales are immutable except through dedicated lifecycle operations.

---

# 20. Payments

Supported Version 1 payment methods:

Cash
Card
Other

Suggested Payment fields:

Id
SaleId
Method
AmountApplied
TenderedAmount
ChangeAmount
ExternalReference
Status
CreatedAtUtc

Card and Other payments:

TenderedAmount == AmountApplied

Cash payments may provide:

TenderedAmount >= AmountApplied

ChangeAmount:

TenderedAmount - AmountApplied

Payment totals applied to the Sale must equal the Sale Total exactly before completion succeeds.

Multiple payments may be used for one Sale.

This enables split-tender scenarios such as:

Cash + Card

No real payment gateway is integrated in Version 1.

ExternalReference exists so a future provider transaction ID can be recorded.

---

# 21. Transactional Sale Completion

Completing a Sale is a critical transaction.

Conceptual workflow:

Load Open Sale
↓
Validate active Cashier Shift
↓
Validate Sale lines
↓
Revalidate Product state
↓
Calculate authoritative totals
↓
Validate payments
↓
Generate Receipt Number
↓
Persist payments
↓
Transition Open → Completed
↓
Commit transaction

Failure must leave the Sale safely Open.

Partial completion must never be persisted.

Use database transactions.

Concurrency must prevent two simultaneous completion requests from both succeeding.

---

# 22. Completion Idempotency

Sale completion should protect against accidental duplicate submission.

Use an explicit idempotency mechanism or an equivalent database-backed guarantee.

The same successful completion request must not create:

- duplicate Payments
- duplicate Receipts
- duplicate completed Sales

---

# 23. Receipts

Receipt information is represented by the immutable completed Sale and its historical SaleLines/Payments.

Do not create a separate Receipt entity unless implementation requirements justify it.

Receipt output should include:

- Receipt Number
- Branch
- Register
- Cashier
- Shift
- Sale timestamp
- Sale lines
- Discounts
- Taxes
- Payments
- Cash tender
- Change
- Totals

---

# 24. Sale Voiding

Void is a privileged operation.

Allowed for:

Admin
Manager

Version 1 rules:

- Only Completed Sales may be voided.
- A Sale with existing refunds cannot be voided.
- A Sale may only be voided once.
- A void reason is required.
- Original Sale and Payment records remain preserved.
- Status changes to Voided.
- Void actor and timestamp are recorded.

Voided sales must be treated correctly in reporting and shift reconciliation.

---

# 25. Refunds

Support:

Partial Refund
Full Refund

A refund references the original completed Sale.

RefundLine records the quantity being returned from each SaleLine.

Rules:

- Refund quantities must be positive.
- Total refunded quantity cannot exceed originally sold quantity.
- Repeated refunds must consider previous Refunds.
- Refund financial values are derived from historical SaleLine snapshots.
- Current Product prices, Tax Rates, or Discounts must never affect Refund calculations.
- Refunds cannot be processed against Voided Sales.
- Concurrent refund requests must not over-refund a Sale.

When all refundable quantities have been refunded:

SaleStatus → Refunded

Otherwise:

SaleStatus → PartiallyRefunded

---

# 26. Refund Payments

RefundPayment records how money was returned.

Suggested fields:

Id
RefundId
OriginalPaymentId
Method
Amount
ExternalReference
CreatedAtUtc

The total refunded payment amount must equal the Refund total.

Version 1 does not call external card-payment APIs.

External references may be recorded for simulated/provider-managed operations.

---

# 27. Cashier Shift Closing

Closing a Shift calculates cash reconciliation.

Expected Cash:

Opening Float

- Completed Cash Sales

* Cash Refunds
* Cash effects of Voided Sales

Declared Cash is entered by the Cashier.

Cash Variance:

Declared Cash - Expected Cash

The API calculates ExpectedCash and CashVariance server-side.

The client cannot provide authoritative expected cash.

A Shift cannot close while it contains unfinished Open Sales.

Closing is transactional and concurrency-safe.

---

# 28. Reporting

Use Dapper for focused reporting.

EF Core remains the primary persistence technology.

Suggested Admin/Manager reporting:

GET /api/admin/reports/sales-summary

Filters may include:

fromDate
toDate
branchId
registerId
cashierUserId

Suggested metrics:

- Completed sales count
- Gross sales
- Discount total
- Tax total
- Sales total
- Void total
- Refund total
- Net sales
- Cash payments
- Card payments
- Other payments
- Top products

A second focused report may summarize Cashier Shifts and cash variance if justified.

Use parameterized SQL.

Do not implement a generic reporting engine.

---

# 29. Pagination, Filtering, and Sorting

Use database-side pagination for operational lists.

Examples:

- Sales
- Employee accounts
- Products
- Shifts

Use explicit sorting whitelists.

Do not support arbitrary property-name sorting.

Return deterministic ordering with ID tie-breakers.

---

# 30. Validation

Use FluentValidation for request validation.

Examples:

- Required strings
- String length limits
- Positive quantities
- Non-negative money
- Percentage ranges
- Pagination bounds
- Valid enum values
- Date ranges
- Payment amounts
- Refund quantities

Business-rule conflicts should normally return:

409 Conflict

Request-shape validation should return:

400 Bad Request

Use consistent Problem Details responses.

---

# 31. Security

Do not expose:

- Password hashes
- JWT signing keys
- Refresh-token hashes
- Raw stored refresh tokens
- SQL connection strings
- Internal exception details
- Stack traces
- Sensitive payment information

Raw refresh tokens are returned only to the authenticated client when issued.

Persist refresh-token hashes rather than raw tokens.

No public Admin, Manager, or Cashier role selection exists.

---

# 32. Testing Strategy

Use automated tests throughout development.

Cover:

- Authentication
- Authorization
- Employee provisioning
- Branch/Register management
- Product configuration
- Tax calculations
- Discount calculations
- Shift opening concurrency
- Sale line calculations
- Historical snapshots
- Sale completion transactions
- Duplicate completion protection
- Split payments
- Cash tender/change
- Void rules
- Partial refunds
- Full refunds
- Refund concurrency
- Shift closing
- Cash reconciliation
- Dapper reports
- Problem Details
- OpenAPI security metadata

Transaction-critical tests must use a relational database.

Use real SQL Server tests where SQL Server locking behavior itself is part of the guarantee.

---

# 33. Version 1 Scope Exclusions

Do not implement:

- Frontend POS UI
- Mobile applications
- Warehouse management
- Purchase orders
- Supplier management
- Inventory transfers
- Stock counts
- Advanced inventory reservations
- Stripe integration
- Square integration
- PayPal integration
- Real card terminal integration
- Receipt printer integration
- Barcode scanner integration
- Offline-first synchronization
- Multi-currency
- Loyalty points
- Gift cards
- Accounting integration
- Invoicing system
- Payroll
- Complex promotion engines
- Customer CRM
- Microservices
- Message brokers
- Redis
- CQRS
- MediatR
- Docker orchestration
- Kubernetes

These may belong to separate projects or later versions.

---

# 34. Development Phases

## Phase 01 — Foundation

Branch:

phase/01-foundation

Implement:

- .NET 10 solution structure
- Web API project
- Test project
- global.json
- EF Core SQL Server
- Core entities
- Entity configurations
- Constraints and indexes
- Initial migration
- OpenAPI
- Scalar
- Health endpoint
- TimeProvider
- Initial automated tests

No authentication endpoints or POS business workflows yet.

---

## Phase 02 — Authentication & Employee Access

Branch:

phase/02-authentication

Implement:

- Login
- JWT access tokens
- Password hashing
- Admin / Manager / Cashier roles
- Current-user service
- Bootstrap Admin strategy
- Admin employee provisioning
- Employee activation/deactivation
- FluentValidation
- Authorization tests
- OpenAPI Bearer support

No refresh-token rotation yet.

---

## Phase 03 — Refresh Tokens

Branch:

phase/03-refresh-tokens

Implement:

- Secure refresh-token generation
- Hash-only persistence
- Rotation
- Revocation
- Replay protection
- Logout
- Concurrent refresh protection
- Session isolation

---

## Phase 04 — POS Configuration

Branch:

phase/04-pos-configuration

Implement:

- Branch management
- Register management
- Product management
- Tax Rate management
- Discount management
- Activation/deactivation
- Validation
- Pagination/filtering where useful

---

## Phase 05 — Cashier Shifts

Branch:

phase/05-cashier-shifts

Implement:

- Open Shift
- View active Shift
- Shift history
- Register/Cashier validation
- Opening float
- One-open-shift-per-register rule
- One-open-shift-per-cashier rule
- Database-backed concurrency protection

Shift closing is deferred to Phase 09.

---

## Phase 06 — Sale Building & Calculations

Branch:

phase/06-sale-building

Implement:

- Create Open Sale
- Add SaleLine
- Update quantity
- Remove SaleLine
- Apply Discount
- Remove Discount
- Historical Product snapshots
- Historical Tax snapshots
- Historical Discount snapshots
- Centralized monetary calculations
- Deterministic rounding
- Sale totals
- Open-Sale concurrency handling

No payments or completion yet.

---

## Phase 07 — Payments & Sale Completion

Branch:

phase/07-sale-completion-payments

Implement:

- Cash payments
- Card payments
- Other payments
- Split payments
- Cash tender
- Change calculation
- Transactional Sale completion
- Receipt number generation
- Idempotency / duplicate-submit protection
- Completed Sale details
- Customer-safe financial snapshots
- Concurrency tests

---

## Phase 08 — Voids & Refunds

Branch:

phase/08-voids-refunds

Implement:

- Manager/Admin voiding
- Void reasons
- Partial refunds
- Full refunds
- RefundLine calculation
- RefundPayment records
- Over-refund prevention
- Concurrent refund protection
- PartiallyRefunded status
- Refunded status
- Historical refund integrity

---

## Phase 09 — Shift Closing & Reconciliation

Branch:

phase/09-shift-closing

Implement:

- Expected cash calculation
- Declared cash
- Cash variance
- Cash sales
- Cash refunds
- Void cash effects
- Open-Sale close protection
- Transactional Shift close
- Closed Shift details
- Reconciliation tests

---

## Phase 10 — Dapper Reporting

Branch:

phase/10-reporting

Implement:

- Admin/Manager reporting authorization
- Dapper sales summary
- Date filtering
- Branch filtering
- Register filtering
- Cashier filtering
- Payment-method totals
- Refund totals
- Void totals
- Net sales
- Top products
- Parameterized SQL
- Reporting tests

Keep reporting intentionally focused.

---

## Phase 11 — API Quality & Portfolio Polish

Branch:

phase/11-portfolio-polish

Implement:

- Centralized exception handling
- Problem Details
- OpenAPI quality
- Scalar verification
- Security audit
- Logging review
- Package vulnerability audit
- README
- Architecture documentation
- Mermaid diagrams
- Real API screenshots
- Final automated regression suite
- Portfolio presentation

No new business features.

---

# 35. Git Workflow

Every implementation Phase follows:

master
↓
create phase branch
↓
implement only that phase
↓
automated tests
↓
manual Scalar verification
↓
build/test/security checks
↓
commit
↓
push
↓
Pull Request
↓
review
↓
merge into master
↓
delete phase branch
↓
verify clean master

Each Pull Request must clearly document:

- What was implemented
- Business rules
- Security considerations
- Concurrency behavior
- Tests
- Manual verification
- Scope exclusions

---

# 36. Final Portfolio Positioning

RetailPOSApi should demonstrate the ability to build a realistic transactional business backend.

The repository should communicate expertise in:

ASP.NET Core

- SQL Server
- EF Core
- JWT Security
- Business Rules
- Transactions
- Concurrency
- Financial Calculations
- Payments
- Refunds
- Dapper Reporting
- Automated Testing

The finished project should be understandable enough for a freelance client to review quickly while still demonstrating production-oriented backend engineering.
