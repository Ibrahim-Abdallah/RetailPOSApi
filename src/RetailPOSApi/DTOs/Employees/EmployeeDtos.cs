using System.Text.Json.Serialization;
using RetailPOSApi.Domain;

namespace RetailPOSApi.DTOs.Employees;

public sealed record CreateEmployeeRequest(string FirstName, string LastName, string Email, string Password, string ConfirmPassword, UserRole Role);
public sealed record ActivationRequest([property: JsonRequired] bool IsActive);
public sealed record EmployeeQuery(int PageNumber = 1, int PageSize = 10, UserRole? Role = null, bool? IsActive = null);
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount);
