using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static class NativePayslipDesignService
{
    public const string Theme = "Excel Payslip";
    public static readonly string[] InfoTokens = ["DESIGNATION", "DEPARTMENT", "GRADE", "WORK_LOCATION", "JOINING_DATE", "EMAIL", "MOBILE", "UAN", "ESIC", "PAN", "AADHAAR", "BANK_ACCOUNT", "BANK_NAME", "IFSC", "ADDRESS", "SAVED_MONTHLY_GROSS", "CURRENT_ANNUAL_CTC", "PAYROLL_DAYS", "PRESENT_DAYS", "PAYABLE_DAYS"];
    public static readonly (string Label,string Token)[] SupplementalFields=[("Designation","DESIGNATION"),("Department","DEPARTMENT"),("Grade","GRADE"),("Work Location","WORK_LOCATION"),("Email","EMAIL"),("Phone","MOBILE"),("Annual CTC","CURRENT_ANNUAL_CTC")];
    internal static bool SourceKeepsMappedZeroRows(bool simpleLayout) => !simpleLayout;
    internal static ExcelPayslipPdfPresentationOptions Presentation(PayslipDesignProfile profile, PayslipDesignTemplate template,
        NativeCurrencyFrozenRow? currency = null) => new()
    {
        ShowLogo = template.ShowLogo, ShowClient = template.ShowClient, KeepMappedZeroRows = profile.KeepMappedZeroRows,
        ExplicitSimpleInformation = true, Footer = template.Note,
        NativeEmployerTotal = currency?.Lines.Where(line => LineKind(line.Category) == "employer").Sum(line => line.AccountedEarned)
    };
    public static string Category(string kind) => kind switch { "earning" => "Earning", "deduction" => "Deduction", "employer" => "Employer", _ => "" };
    public static string LineKind(string category) => category.ToLowerInvariant() switch
    { "earning" or "reimbursement" => "earning", "deduction" => "deduction", "benefit" or "employer" or "employer contribution" or "employercontribution" => "employer", _ => "" };

    public static List<NativePayslipDesignLine> ReadLines(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Saved payroll lines are invalid.");
        string Text(JsonElement value,string name) => value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).Value is var found && found.ValueKind != JsonValueKind.Undefined ? found.ToString() : "";
        decimal Number(JsonElement value,string name) => decimal.TryParse(Text(value,name),NumberStyles.Float,CultureInfo.InvariantCulture,out var number) ? number : throw new InvalidOperationException($"Saved payroll {name} is not numeric.");
        var lines=new List<NativePayslipDesignLine>();
        foreach(var value in document.RootElement.EnumerateArray())
        {
            var category=Text(value,"Category");var code=Text(value,"Id");
            if(category.Equals("Summary",StringComparison.OrdinalIgnoreCase))continue;
            if(LineKind(category)=="")throw new InvalidOperationException($"Saved payroll component {code} has an unsupported financial category; no amount was silently omitted.");
            if(string.IsNullOrWhiteSpace(code))throw new InvalidOperationException("A saved payroll component has no stable code.");
            lines.Add(new(code,category,Number(value,"monthlyAmount"),Number(value,"amount")));
        }
        return lines;
    }

    public static ExcelPayslipRow Project(PayslipDesignProfile profile, PayslipDesignTemplate template, NativePayslipDesignData data)
    {
        ValidateProfile(profile,template);
        if(!NativePayslipDesignScope.Applies(profile,data.Month))throw new InvalidOperationException("This source design does not apply to the saved payroll month.");
        if (data.ClientId != profile.ClientId || data.EmployeeId <= 0) throw new InvalidOperationException("The saved payroll and payslip design belong to different clients.");
        if (data.Status is not ("Approved" or "Partially Paid" or "Paid")) throw new InvalidOperationException("Only approved payroll can use the final payslip design.");
        var mapped = profile.Fields.Where(f => f.Kind != "info").Select(f => (f.Kind,f.NativeCode)).ToHashSet();
        foreach (var line in data.Lines.Where(line => line.Amount != 0))
            if (!mapped.Contains((LineKind(line.Category),line.Code))) throw new InvalidOperationException($"Saved component {line.Code} is not mapped in this payslip design. Review the presentation mapping before downloading.");
        var earnings=data.Lines.Where(l=>LineKind(l.Category)=="earning").Sum(l=>l.Amount);
        var deductions=data.Lines.Where(l=>LineKind(l.Category)=="deduction").Sum(l=>l.Amount);
        if (decimal.Round(earnings-data.Gross,2)!=0 || decimal.Round(deductions-data.Deductions,2)!=0 || decimal.Round(data.Gross-data.Deductions-data.NetPay,2)!=0)
            throw new InvalidOperationException("Saved payroll component totals do not reconcile with the approved payslip. No source salary was substituted.");
        var row=new ExcelPayslipRow { Id=$"native-{data.EmployeeId}",EmployeeCode=data.EmployeeCode,EmployeeName=data.EmployeeName,NetPay=data.NetPay,DeclaredGross=data.Gross,DeclaredDeductions=data.Deductions };
        foreach(var field in profile.Fields.Where(f=>f.Visible))
        {
            if(field.Kind=="info")
            {
                if(!template.ShowBank && field.InfoToken is "BANK_ACCOUNT" or "BANK_NAME" or "IFSC")continue;
                var value=NativePayslipMonthlyComponentInformation.TryCode(field.InfoToken,out _)
                    ?NativePayslipMonthlyComponentInformation.ReadSavedMonthly(field.InfoToken,data.Lines)
                    :data.Information.GetValueOrDefault(field.InfoToken,"");
                if(field.SourceColumn>=1500 && string.IsNullOrWhiteSpace(value))continue;
                row.Information.Add(new ExcelPayslipText { Label=field.Label,Value=value });
                continue;
            }
            var matches=data.Lines.Where(l=>l.Code==field.NativeCode && LineKind(l.Category)==field.Kind).ToArray();
            // A mapped component absent from this employee's saved lines is a verified zero,
            // never a salary amount taken from the source sheet or current salary master.
            var amount=new ExcelPayslipAmount { Label=field.Label,Amount=matches.Sum(l=>l.Amount) };
            (field.Kind=="earning"?row.Earnings:field.Kind=="deduction"?row.Deductions:row.EmployerContributions).Add(amount);
        }
        return row;
    }

    // Called only after the repository authenticates the complete processing receipt
    // in its current transaction. Financial validation always uses saved accounting;
    // the original native earned stage changes only the displayed component value.
    internal static ExcelPayslipRow ProjectWithFrozenCurrency(PayslipDesignProfile profile, PayslipDesignTemplate template,
        NativePayslipDesignData data, NativeCurrencyFrozenRow? currency)
    {
        var row = Project(profile, template, data);
        if (currency is null) return row;
        if (profile.AmountDecimalPlaces != 0 || data.NativeRowId <= 0 || currency.NativeRowId != data.NativeRowId
            || currency.EmployeeId != data.EmployeeId || currency.Lines.IsDefault || currency.Lines.Length != data.Lines.Count
            || currency.Lines.Select(line => line.Code).Distinct(StringComparer.Ordinal).Count() != currency.Lines.Length)
            throw new InvalidOperationException("The frozen native currency row does not match this saved payslip.");
        foreach (var saved in data.Lines)
        {
            var frozen = currency.Lines.SingleOrDefault(line => line.Code == saved.Code);
            if (frozen is null || frozen.Category != saved.Category || frozen.AccountedMonthly != saved.MonthlyAmount
                || frozen.AccountedEarned != saved.Amount || NativePayslipCurrencyStage.AccountedMonthly(frozen.MonthlyRaw) != saved.MonthlyAmount
                || NativePayslipCurrencyStage.AccountedEarned(frozen.UnroundedEarned, frozen.AccountingPath) != saved.Amount
                || frozen.AccountingPath == "Monthly" && frozen.UnroundedEarned != frozen.MonthlyRaw)
                throw new InvalidOperationException("The frozen native stage differs from saved component accounting.");
        }
        foreach (var kind in new[] { "earning", "deduction", "employer" })
        {
            var amounts = kind == "earning" ? row.Earnings : kind == "deduction" ? row.Deductions : row.EmployerContributions;
            var fields = profile.Fields.Where(field => field.Visible && field.Kind == kind).ToArray();
            for (var index = 0; index < fields.Length; index++)
            {
                var line = currency.Lines.SingleOrDefault(line => line.Code == fields[index].NativeCode && LineKind(line.Category) == kind);
                // An absent zero mapping retains Project's existing zero behavior.
                if (line is not null) amounts[index].Amount = line.AccountingPath == "Monthly"
                    ? Math.Max(0, line.MonthlyRaw) : line.UnroundedEarned;
            }
        }
        return row;
    }

    public static void ValidateProfile(PayslipDesignProfile profile,PayslipDesignTemplate template)
    {
        if(profile.Version!=1 || profile.ClientId<=0 || profile.TemplateId!=template.Id || profile.ClientId!=template.ClientId || template.Theme!=Theme)
            throw new InvalidOperationException("The reviewed payslip design is not valid for this client/template.");
        if(profile.SourceKind is not ("ExcelSource" or CorePayrollPayslipDesign.SourceKind))
            throw new InvalidOperationException("The reviewed payslip design source is unsupported.");
        if(profile.SourceKind==CorePayrollPayslipDesign.SourceKind && (profile.SourceBatchId!=""
            || profile.SourceHash!=CorePayrollPayslipDesign.SchemaHash(profile.ClientId,profile.Fields,profile.SimpleLayout,profile.KeepMappedZeroRows)))
            throw new InvalidOperationException("The core payroll design metadata is invalid for this client.");
        NativePayslipDesignScope.ValidatePeriods(profile.PayPeriods);
        NativePayslipDesignScope.ValidateFallback(profile);
        if(profile.AmountDecimalPlaces is not (0 or 2) || profile.Fields.Count is <1 or >100) throw new InvalidOperationException("Choose a supported precision and a bounded set of presentation fields.");
        if(profile.Fields.Select(f=>f.SourceColumn).Distinct().Count()!=profile.Fields.Count)throw new InvalidOperationException("A source column can be mapped only once.");
        var money=new HashSet<(string,string)>();
        foreach(var field in profile.Fields)
        {
            if(field.SourceColumn<0 || field.SourceColumn>=1500+SupplementalFields.Length || string.IsNullOrWhiteSpace(field.Label) || field.Label.Length>150)throw new InvalidOperationException("A payslip design field has an invalid column or label.");
            if(field.SourceColumn>=1500 && (field.Kind!="info" || field.Label!=SupplementalFields[field.SourceColumn-1500].Label || field.InfoToken!=SupplementalFields[field.SourceColumn-1500].Token))
                throw new InvalidOperationException("Supplemental Master fields must retain their reviewed fixed label and information token.");
            if(field.Kind=="info")
            { if((field.Visible || field.InfoToken!="") && !NativePayslipMonthlyComponentInformation.SupportedToken(field.InfoToken) || field.NativeCode!="")throw new InvalidOperationException("Select a supported Master/payroll information token."); }
            else if(Category(field.Kind)=="" || string.IsNullOrWhiteSpace(field.NativeCode) || field.NativeCode.Length>100 || field.InfoToken!="" || !money.Add((field.Kind,field.NativeCode)))
                throw new InvalidOperationException("Review every component mapping; duplicate or empty native codes are not allowed.");
        }
    }
}
