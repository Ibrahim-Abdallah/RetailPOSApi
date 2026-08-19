using RetailPOSApi.Domain;

namespace RetailPOSApi.Services;

public static class SaleCalculation
{
    public const decimal MaximumMoney = 9_999_999_999_999_999.99m;
    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static bool TryRecalculateLine(SaleLine line)
    {
        try
        {
            var discount = line.DiscountType switch
            {
                Domain.DiscountType.Percentage => checked(line.UnitPrice * (line.DiscountValue ?? 0) / 100m),
                Domain.DiscountType.FixedAmount => line.DiscountValue ?? 0,
                _ => 0m
            };
            line.UnitDiscountAmount = Money(Math.Min(line.UnitPrice, Math.Max(0m, discount)));
            line.UnitNetAmount = Money(checked(line.UnitPrice - line.UnitDiscountAmount));
            line.UnitTaxAmount = Money(checked(line.UnitNetAmount * line.TaxRatePercentage / 100m));
            line.UnitTotal = Money(checked(line.UnitNetAmount + line.UnitTaxAmount));
            line.LineSubtotal = Money(checked(line.UnitPrice * line.Quantity));
            line.LineDiscountTotal = Money(checked(line.UnitDiscountAmount * line.Quantity));
            line.LineTaxTotal = Money(checked(line.UnitTaxAmount * line.Quantity));
            line.LineTotal = Money(checked(line.UnitTotal * line.Quantity));
            return ValuesFit(line.UnitPrice, line.UnitDiscountAmount, line.UnitNetAmount, line.UnitTaxAmount,
                line.UnitTotal, line.LineSubtotal, line.LineDiscountTotal, line.LineTaxTotal, line.LineTotal);
        }
        catch (OverflowException) { return false; }
    }

    public static bool TryRecalculateSale(Sale sale)
    {
        try
        {
            sale.Subtotal = Money(sale.Lines.Aggregate(0m, (sum, x) => checked(sum + x.LineSubtotal)));
            sale.DiscountTotal = Money(sale.Lines.Aggregate(0m, (sum, x) => checked(sum + x.LineDiscountTotal)));
            sale.TaxTotal = Money(sale.Lines.Aggregate(0m, (sum, x) => checked(sum + x.LineTaxTotal)));
            sale.TotalAmount = Money(sale.Lines.Aggregate(0m, (sum, x) => checked(sum + x.LineTotal)));
            return ValuesFit(sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.TotalAmount);
        }
        catch (OverflowException) { return false; }
    }

    static bool ValuesFit(params decimal[] values) => values.All(x => x >= 0 && x <= MaximumMoney);
}
