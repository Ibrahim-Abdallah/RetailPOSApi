# API workflows

## Authentication and employees

1. `POST /api/auth/login` validates credentials and returns an access/refresh pair.
2. `POST /api/auth/refresh` rotates a valid refresh token. Reuse of a replaced token triggers descendant revocation; stored values are hashes only.
3. `POST /api/auth/logout` revokes the presented refresh session.
4. A trusted Admin provisions employees through `POST /api/admin/employees`; there is no public registration or role selection.

## Shift and sale

1. A Cashier opens a shift at `POST /api/cashier/shifts/open`. The server validates active branch/register context and one-open-shift constraints.
2. `POST /api/cashier/sales` creates an open sale from the authenticated cashier's active shift.
3. Line endpoints add products, change quantity, and apply/remove one configured discount. The server snapshots catalog data and recalculates totals.
4. `POST /api/cashier/sales/{id}/complete` supplies one or more Cash/Card/Other tenders and an idempotency key. Applied amounts must equal the authoritative total. Cash may exceed the applied amount; the difference is change. Completion atomically stores payments, receipt number, and status.
5. The completed sale response is the receipt record: context, immutable lines, totals, tenders, and change.

## Voids and refunds

Admin or Manager may void an eligible completed sale with a required reason. A sale with refunds cannot be voided. For refunds, requested quantities and repayment allocation are checked against the historical sale and all earlier refunds. A full refund moves a completed sale directly to `Refunded`; partial returns lead to `PartiallyRefunded`, and exhausting the remaining quantity later leads to `Refunded`. Concurrent over-refunds are rejected.

## Closing and reporting

A Cashier closes their shift with declared cash only after resolving open sales. The API derives expected cash and variance and commits the close transaction. Admin/Manager reports at `/api/management/reports/sales-summary` and `/shift-summary` accept optional `[fromDate, toDate)` and operational filters. Sales, voids, and refunds use their respective activity timestamps; SQL parameters are never concatenated from client values.
