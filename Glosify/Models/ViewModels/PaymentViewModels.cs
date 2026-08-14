namespace Glosify.Models.ViewModels;

public sealed class PaymentIndexViewModel
{
    public bool IsEnabled { get; set; }
    public IReadOnlyList<PaymentPackageViewModel> Packages { get; set; } = [];
}

public sealed class PaymentPackageViewModel
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayPrice { get; set; } = string.Empty;
    public int Credits { get; set; }
}

public sealed class PaymentSuccessViewModel
{
    public bool IsPaid { get; set; }
    public bool WasFulfilled { get; set; }
    public string Message { get; set; } = string.Empty;
}
