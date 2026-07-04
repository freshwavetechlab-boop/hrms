namespace Payroll.API.Models;

public class ClientBillingModule
{
    public bool IsEnabled { get; set; }
}

public class ClientBillingConfiguration
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int? WorkLocationId { get; set; }
    public string WorkLocationName { get; set; } = string.Empty;
    public string RateCardType { get; set; } = "All";
    public string RateType { get; set; } = "Percentage";
    public decimal Value { get; set; }
    public bool TaxInclusive { get; set; }
    public decimal GstRatePercent { get; set; } = 18m;
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
