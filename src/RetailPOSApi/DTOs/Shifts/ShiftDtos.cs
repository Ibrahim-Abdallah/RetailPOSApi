using RetailPOSApi.Domain;
using System.Text.Json.Serialization;

namespace RetailPOSApi.DTOs.Shifts;

public sealed record OpenShiftRequest(int RegisterId, [property: JsonRequired] decimal OpeningFloat);
public sealed record CloseShiftRequest([property: JsonRequired] decimal DeclaredCash);

public record ShiftQuery(
    int Page = 1,
    int PageSize = 20,
    CashierShiftStatus? Status = null,
    string SortBy = "openedAt",
    string SortDirection = "desc");

public sealed record ManagementShiftQuery(
    int Page = 1,
    int PageSize = 20,
    CashierShiftStatus? Status = null,
    string SortBy = "openedAt",
    string SortDirection = "desc",
    int? BranchId = null,
    int? RegisterId = null,
    int? CashierUserId = null) : ShiftQuery(Page, PageSize, Status, SortBy, SortDirection);

public sealed record ShiftResponse(
    int Id,
    int BranchId,
    string BranchCode,
    string BranchName,
    int RegisterId,
    string RegisterCode,
    string RegisterName,
    int CashierUserId,
    string CashierName,
    CashierShiftStatus Status,
    decimal OpeningFloat,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    decimal? DeclaredCash,
    decimal? ExpectedCash,
    decimal? CashVariance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
