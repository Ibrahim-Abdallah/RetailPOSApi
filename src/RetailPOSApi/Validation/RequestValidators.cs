using FluentValidation;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Employees;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Validation;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty();
    }
}

public abstract class ConfigurationQueryValidatorBase<T> : AbstractValidator<T>
    where T : ConfigurationQuery
{
    protected ConfigurationQueryValidatorBase(params string[] allowedSortFields)
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.SortDirection)
            .Must(direction => direction.Equals("asc", StringComparison.OrdinalIgnoreCase) ||
                               direction.Equals("desc", StringComparison.OrdinalIgnoreCase));
        RuleFor(query => query.SortBy)
            .Must(sortBy => allowedSortFields.Contains(sortBy, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class BranchQueryValidator : ConfigurationQueryValidatorBase<BranchQuery>
{
    public BranchQueryValidator() : base("name", "code", "createdAt") { }
}

public sealed class RegisterQueryValidator : ConfigurationQueryValidatorBase<RegisterQuery>
{
    public RegisterQueryValidator() : base("name", "code", "createdAt") =>
        RuleFor(query => query.BranchId).GreaterThan(0).When(query => query.BranchId.HasValue);
}

public sealed class ProductQueryValidator : ConfigurationQueryValidatorBase<ProductQuery>
{
    public ProductQueryValidator() : base("name", "sku", "unitPrice", "createdAt") =>
        RuleFor(query => query.TaxRateId).GreaterThan(0).When(query => query.TaxRateId.HasValue);
}

public sealed class TaxRateQueryValidator : ConfigurationQueryValidatorBase<TaxRateQuery>
{
    public TaxRateQueryValidator() : base("name", "percentage", "createdAt") { }
}

public sealed class DiscountQueryValidator : ConfigurationQueryValidatorBase<DiscountQuery>
{
    public DiscountQueryValidator() : base("name", "value", "createdAt") =>
        RuleFor(query => query.Type).IsInEnum().When(query => query.Type.HasValue);
}

public sealed class BranchRequestValidator : AbstractValidator<BranchRequest>
{
    public BranchRequestValidator()
    {
        NormalizedRequired(RuleFor(request => request.Name), 200);
        NormalizedRequired(RuleFor(request => request.Code), 50);
        NormalizedRequired(RuleFor(request => request.Address), 500);
    }

    private static void NormalizedRequired(IRuleBuilderInitial<BranchRequest, string> rule, int maximumLength) =>
        rule.Must(value => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength);
}

public sealed class CreateRegisterRequestValidator : AbstractValidator<CreateRegisterRequest>
{
    public CreateRegisterRequestValidator()
    {
        RuleFor(request => request.BranchId).GreaterThan(0);
        RuleFor(request => request.Name).Must(value => NormalizedRequired(value, 200));
        RuleFor(request => request.Code).Must(value => NormalizedRequired(value, 50));
    }

    private static bool NormalizedRequired(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;
}

public sealed class UpdateRegisterRequestValidator : AbstractValidator<UpdateRegisterRequest>
{
    public UpdateRegisterRequestValidator()
    {
        RuleFor(request => request.Name).Must(value => NormalizedRequired(value, 200));
        RuleFor(request => request.Code).Must(value => NormalizedRequired(value, 50));
    }

    private static bool NormalizedRequired(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;
}

public sealed class TaxRateRequestValidator : AbstractValidator<TaxRateRequest>
{
    public TaxRateRequestValidator()
    {
        RuleFor(request => request.Name).Must(value => NormalizedRequired(value, 200));
        RuleFor(request => request.Percentage).InclusiveBetween(0, 100);
    }

    private static bool NormalizedRequired(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;
}

public sealed class DiscountRequestValidator : AbstractValidator<DiscountRequest>
{
    public DiscountRequestValidator()
    {
        RuleFor(request => request.Name).Must(value => NormalizedRequired(value, 200));
        RuleFor(request => request.Type).IsInEnum();
        RuleFor(request => request.Value).GreaterThanOrEqualTo(0);
        RuleFor(request => request.Value).LessThanOrEqualTo(100)
            .When(request => request.Type == DiscountType.Percentage);
        RuleFor(request => request.Value).Must(value => Math.Round(value, 2, MidpointRounding.AwayFromZero) <= 99_999.99m)
            .When(request => request.Type == DiscountType.FixedAmount && request.Value >= 0)
            .WithMessage("Fixed amount exceeds the supported precision.");
    }

    private static bool NormalizedRequired(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;
}

public sealed class ProductRequestValidator : AbstractValidator<ProductRequest>
{
    public ProductRequestValidator()
    {
        RuleFor(request => request.Sku).Must(value => NormalizedRequired(value, 100));
        RuleFor(request => request.Barcode)
            .Must(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length <= 100);
        RuleFor(request => request.Name).Must(value => NormalizedRequired(value, 300));
        RuleFor(request => request.UnitPrice).GreaterThanOrEqualTo(0);
        RuleFor(request => request.UnitPrice)
            .Must(value => Math.Round(value, 2, MidpointRounding.AwayFromZero) <= 9_999_999_999_999_999.99m)
            .When(request => request.UnitPrice >= 0)
            .WithMessage("Unit price exceeds the supported precision.");
        RuleFor(request => request.TaxRateId).GreaterThan(0);
    }

    private static bool NormalizedRequired(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;
}

public sealed class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
}

public sealed class CreateEmployeeRequestValidator : AbstractValidator<CreateEmployeeRequest>
{
    public CreateEmployeeRequestValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128)
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.")
            .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain a special character.");
        RuleFor(x => x.ConfirmPassword).Equal(x => x.Password)
            .WithMessage("Passwords do not match.");
        RuleFor(x => x.Role).IsInEnum();
    }
}

public sealed class EmployeeQueryValidator : AbstractValidator<EmployeeQuery>
{
    public EmployeeQueryValidator()
    {
        RuleFor(x => x.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Role).IsInEnum().When(x => x.Role.HasValue);
    }
}
