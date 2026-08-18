using RetailPOSApi.Domain;

namespace RetailPOSApi.Tests;

public sealed class EnumStabilityTests
{
    [Fact]
    public void Persisted_enum_values_are_stable()
    {
        Assert.Equal([1, 2, 3], Enum.GetValues<UserRole>().Select(x => (int)x));
        Assert.Equal([1, 2], Enum.GetValues<CashierShiftStatus>().Select(x => (int)x));
        Assert.Equal([1, 2, 3, 4, 5], Enum.GetValues<SaleStatus>().Select(x => (int)x));
        Assert.Equal([1, 2, 3], Enum.GetValues<PaymentMethod>().Select(x => (int)x));
        Assert.Equal([1, 2, 3, 4], Enum.GetValues<PaymentStatus>().Select(x => (int)x));
        Assert.Equal([1, 2], Enum.GetValues<DiscountType>().Select(x => (int)x));
        Assert.Equal([1, 2, 3], Enum.GetValues<RefundStatus>().Select(x => (int)x));
    }
}
