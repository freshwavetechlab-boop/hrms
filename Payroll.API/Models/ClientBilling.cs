namespace Payroll.API.Models;

public class ClientBillingModule
{
    public bool IsEnabled { get; set; }
    public bool AdvancedCostingEnabled { get; set; }
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

public class ClientBillingAdvancedSetup
{
    public IEnumerable<ClientBillingCostRuleHeader> Headers { get; set; } = [];
    public IEnumerable<ClientBillingCostRuleLine> Lines { get; set; } = [];
}

public class ClientBillingCostRuleHeader
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public int? WorkLocationId { get; set; }
    public string WorkLocationName { get; set; } = "";
    public string RuleName { get; set; } = "";
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public DateTime? EffectiveTo { get; set; }
    public decimal GstRatePercent { get; set; } = 18m;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ClientBillingCostRuleLine
{
    public long Id { get; set; }
    public long HeaderId { get; set; }
    public string RuleName { get; set; } = "";
    public string LineType { get; set; } = "Component Category";
    public string MatchValue { get; set; } = "";
    public string BillingTreatment { get; set; } = "Bill Actual";
    public string BaseType { get; set; } = "Processed Amount";
    public string RateType { get; set; } = "Actual";
    public decimal RateValue { get; set; }
    public bool TaxApplicable { get; set; } = true;
    public bool CommissionApplicable { get; set; } = true;
    public string DisplayGroup { get; set; } = "Salary";
    public int SortOrder { get; set; } = 100;
    public bool IsActive { get; set; } = true;
}
