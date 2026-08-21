using FluentValidation;
using RetailPOSApi.DTOs.Auth;
using RetailPOSApi.DTOs.Employees;
using RetailPOSApi.DTOs.Configuration;
using RetailPOSApi.Domain;
using RetailPOSApi.DTOs.Shifts;
using RetailPOSApi.DTOs.Sales;
using RetailPOSApi.Services;

namespace RetailPOSApi.Validation;

public sealed class AddSaleLineRequestValidator : AbstractValidator<AddSaleLineRequest>
{
    public AddSaleLineRequestValidator()
    {
        RuleFor(x => x.ProductId).GreaterThan(0);
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.DiscountId).GreaterThan(0).When(x => x.DiscountId.HasValue);
    }
}
public sealed class UpdateSaleLineQuantityRequestValidator : AbstractValidator<UpdateSaleLineQuantityRequest>
{
    public UpdateSaleLineQuantityRequestValidator() => RuleFor(x => x.Quantity).GreaterThan(0);
}
public sealed class ApplySaleLineDiscountRequestValidator : AbstractValidator<ApplySaleLineDiscountRequest>
{
    public ApplySaleLineDiscountRequestValidator() => RuleFor(x => x.DiscountId).GreaterThan(0);
}
public sealed class CompleteSaleRequestValidator : AbstractValidator<CompleteSaleRequest>
{
    public CompleteSaleRequestValidator()
    {
        RuleFor(x => x.IdempotencyKey)
            .Must(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length <= 100)
            .WithMessage("Idempotency key is required and must not exceed 100 characters.");
        RuleFor(x => x.Payments).NotNull();
        RuleForEach(x => x.Payments).SetValidator(new CompleteSalePaymentRequestValidator());
    }
}
public sealed class CompleteSalePaymentRequestValidator : AbstractValidator<CompleteSalePaymentRequest>
{
    public CompleteSalePaymentRequestValidator()
    {
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.AmountApplied).GreaterThan(0).Must(MoneyValue)
            .WithMessage("Amount applied must fit decimal(18,2) without rounding.");
        RuleFor(x => x.TenderedAmount).GreaterThanOrEqualTo(0).Must(MoneyValue)
            .WithMessage("Tendered amount must fit decimal(18,2) without rounding.");
        RuleFor(x => x).Must(x => x.Method != PaymentMethod.Cash || x.TenderedAmount >= x.AmountApplied)
            .WithMessage("Cash tendered amount must be at least the amount applied.");
        RuleFor(x => x).Must(x => x.Method is not (PaymentMethod.Card or PaymentMethod.Other) || x.TenderedAmount == x.AmountApplied)
            .WithMessage("Card and Other tendered amount must equal the amount applied.");
        RuleFor(x => x.ExternalReference).Must(x => x is null || x.Trim().Length <= 200)
            .WithMessage("External reference must not exceed 200 characters.");
    }

    static bool MoneyValue(decimal value) => value <= SaleCalculation.MaximumMoney && (decimal.GetBits(value)[3] >> 16 & 0x7F) <= 2;
}
public sealed class VoidSaleRequestValidator : AbstractValidator<VoidSaleRequest>
{
    public VoidSaleRequestValidator() => RuleFor(x => x.Reason).Must(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length <= 500)
        .WithMessage("Reason is required and must not exceed 500 characters.");
}
public sealed class ProcessRefundRequestValidator : AbstractValidator<ProcessRefundRequest>
{
    public ProcessRefundRequestValidator()
    {
        RuleFor(x => x.Reason).Must(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length <= 500)
            .WithMessage("Reason is required and must not exceed 500 characters.");
        RuleFor(x => x.Lines).NotNull().NotEmpty();
        RuleForEach(x => x.Lines).SetValidator(new ProcessRefundLineRequestValidator());
        RuleFor(x => x.Lines).Must(x => x is null || x.Select(y => y.SaleLineId).Distinct().Count() == x.Count)
            .WithMessage("Duplicate sale line ids are not allowed.");
        RuleFor(x => x.Payments).NotNull();
        RuleForEach(x => x.Payments).SetValidator(new ProcessRefundPaymentRequestValidator());
        RuleFor(x => x.Payments).Must(x => x is null || x.Select(y => y.OriginalPaymentId).Distinct().Count() == x.Count)
            .WithMessage("Duplicate original payment ids are not allowed.");
    }
}
public sealed class ProcessRefundLineRequestValidator : AbstractValidator<ProcessRefundLineRequest>
{
    public ProcessRefundLineRequestValidator() { RuleFor(x => x.SaleLineId).GreaterThan(0); RuleFor(x => x.Quantity).GreaterThan(0); }
}
public sealed class ProcessRefundPaymentRequestValidator : AbstractValidator<ProcessRefundPaymentRequest>
{
    public ProcessRefundPaymentRequestValidator()
    {
        RuleFor(x => x.OriginalPaymentId).GreaterThan(0);
        RuleFor(x => x.Amount).GreaterThan(0).Must(x => x <= SaleCalculation.MaximumMoney && (decimal.GetBits(x)[3] >> 16 & 0x7F) <= 2)
            .WithMessage("Amount must fit decimal(18,2) without rounding.");
        RuleFor(x => x.ExternalReference).Must(x => x is null || x.Trim().Length <= 200)
            .WithMessage("External reference must not exceed 200 characters.");
    }
}
public class SaleQueryValidator : AbstractValidator<SaleQuery>
{
    public SaleQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.CashierShiftId).GreaterThan(0).When(x => x.CashierShiftId.HasValue);
        RuleFor(x => x.SortBy).NotEmpty().Must(x => x is not null && new[] { "createdAt", "totalAmount" }.Contains(x, StringComparer.OrdinalIgnoreCase));
        RuleFor(x => x.SortDirection).NotEmpty().Must(x => x is not null && (x.Equals("asc", StringComparison.OrdinalIgnoreCase) || x.Equals("desc", StringComparison.OrdinalIgnoreCase)));
    }
}
public sealed class ManagementSaleQueryValidator : AbstractValidator<ManagementSaleQuery>
{
    public ManagementSaleQueryValidator()
    {
        Include(new SaleQueryValidator());
        RuleFor(x => x.BranchId).GreaterThan(0).When(x => x.BranchId.HasValue);
        RuleFor(x => x.RegisterId).GreaterThan(0).When(x => x.RegisterId.HasValue);
        RuleFor(x => x.CashierUserId).GreaterThan(0).When(x => x.CashierUserId.HasValue);
    }
}

public sealed class OpenShiftRequestValidator : AbstractValidator<OpenShiftRequest>
{
    public OpenShiftRequestValidator()
    {
        RuleFor(x => x.RegisterId).GreaterThan(0);
        RuleFor(x => x.OpeningFloat).GreaterThanOrEqualTo(0);
        RuleFor(x => x.OpeningFloat)
            .Must(value => Math.Round(value, 2, MidpointRounding.AwayFromZero) <= 9_999_999_999_999_999.99m)
            .When(x => x.OpeningFloat >= 0)
            .WithMessage("Opening float exceeds the supported precision.");
    }
}

public sealed class CloseShiftRequestValidator : AbstractValidator<CloseShiftRequest>
{
    public CloseShiftRequestValidator()
    {
        RuleFor(x => x.DeclaredCash).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DeclaredCash)
            .Must(value => Math.Round(value, 2, MidpointRounding.AwayFromZero) <= 9_999_999_999_999_999.99m)
            .When(x => x.DeclaredCash >= 0)
            .WithMessage("Declared cash exceeds the supported precision.");
    }
}

public class ShiftQueryValidator : AbstractValidator<ShiftQuery>
{
    public ShiftQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.SortBy).NotEmpty().Must(value => value is not null && new[] { "openedAt", "createdAt" }.Contains(value, StringComparer.OrdinalIgnoreCase));
        RuleFor(x => x.SortDirection).NotEmpty().Must(value => value is not null && (value.Equals("asc", StringComparison.OrdinalIgnoreCase) || value.Equals("desc", StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed class ManagementShiftQueryValidator : AbstractValidator<ManagementShiftQuery>
{
    public ManagementShiftQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.SortBy).NotEmpty().Must(value => value is not null && new[] { "openedAt", "createdAt", "openingFloat" }.Contains(value, StringComparer.OrdinalIgnoreCase));
        RuleFor(x => x.SortDirection).NotEmpty().Must(value => value is not null && (value.Equals("asc", StringComparison.OrdinalIgnoreCase) || value.Equals("desc", StringComparison.OrdinalIgnoreCase)));
        RuleFor(x => x.BranchId).GreaterThan(0).When(x => x.BranchId.HasValue);
        RuleFor(x => x.RegisterId).GreaterThan(0).When(x => x.RegisterId.HasValue);
        RuleFor(x => x.CashierUserId).GreaterThan(0).When(x => x.CashierUserId.HasValue);
    }
}

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
