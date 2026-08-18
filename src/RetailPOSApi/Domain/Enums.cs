namespace RetailPOSApi.Domain;

public enum UserRole { Admin = 1, Manager = 2, Cashier = 3 }
public enum CashierShiftStatus { Open = 1, Closed = 2 }
public enum SaleStatus { Open = 1, Completed = 2, Voided = 3, PartiallyRefunded = 4, Refunded = 5 }
public enum PaymentMethod { Cash = 1, Card = 2, Other = 3 }
public enum PaymentStatus { Pending = 1, Completed = 2, Failed = 3, Refunded = 4 }
public enum DiscountType { Percentage = 1, FixedAmount = 2 }
public enum RefundStatus { Pending = 1, Completed = 2, Failed = 3 }
