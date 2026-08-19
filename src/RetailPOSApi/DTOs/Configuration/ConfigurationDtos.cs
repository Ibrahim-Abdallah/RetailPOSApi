using System.Text.Json.Serialization;
using RetailPOSApi.Domain;

namespace RetailPOSApi.DTOs.Configuration;

public sealed record ActivationRequest([property: JsonRequired] bool IsActive);
public record ConfigurationQuery(int Page = 1, int PageSize = 20, string? Search = null, bool? IsActive = null, string SortBy = "createdAt", string SortDirection = "desc");
public sealed record BranchQuery(int Page = 1, int PageSize = 20, string? Search = null, bool? IsActive = null, string SortBy = "createdAt", string SortDirection = "desc") : ConfigurationQuery(Page, PageSize, Search, IsActive, SortBy, SortDirection);
public sealed record TaxRateQuery(int Page = 1, int PageSize = 20, string? Search = null, bool? IsActive = null, string SortBy = "createdAt", string SortDirection = "desc") : ConfigurationQuery(Page, PageSize, Search, IsActive, SortBy, SortDirection);
public sealed record RegisterQuery(int Page = 1, int PageSize = 20, string? Search = null, bool? IsActive = null, string SortBy = "createdAt", string SortDirection = "desc", int? BranchId = null) : ConfigurationQuery(Page, PageSize, Search, IsActive, SortBy, SortDirection);
public sealed record ProductQuery(int Page = 1, int PageSize = 20, string? Search = null, bool? IsActive = null, string SortBy = "createdAt", string SortDirection = "desc", int? TaxRateId = null) : ConfigurationQuery(Page, PageSize, Search, IsActive, SortBy, SortDirection);
public sealed record DiscountQuery(int Page = 1, int PageSize = 20, string? Search = null, bool? IsActive = null, string SortBy = "createdAt", string SortDirection = "desc", DiscountType? Type = null) : ConfigurationQuery(Page, PageSize, Search, IsActive, SortBy, SortDirection);
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public sealed record BranchRequest(string Name, string Code, string Address);
public sealed record BranchResponse(int Id, string Name, string Code, string Address, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record CreateRegisterRequest(int BranchId, string Name, string Code);
public sealed record UpdateRegisterRequest(string Name, string Code);
public sealed record RegisterResponse(int Id, int BranchId, string BranchCode, string BranchName, string Name, string Code, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record TaxRateRequest(string Name, decimal Percentage);
public sealed record TaxRateResponse(int Id, string Name, decimal Percentage, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record DiscountRequest(string Name, DiscountType Type, decimal Value);
public sealed record DiscountResponse(int Id, string Name, DiscountType Type, decimal Value, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ProductRequest(string Sku, string? Barcode, string Name, decimal UnitPrice, int TaxRateId);
public sealed record ProductResponse(int Id, string Sku, string? Barcode, string Name, decimal UnitPrice, int TaxRateId, string TaxRateName, decimal TaxRatePercentage, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
