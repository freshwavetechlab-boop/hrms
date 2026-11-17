using System.Globalization;
using Payroll.API.Models;

namespace Payroll.API.Services;

// A presentation preview deliberately has no source/native employee values.
// Final register documents continue to use NativePayslipDesignService.Project.
internal static class PayslipDesignSamplePreview
{
    internal static void Validate(PayslipDesignSaveRequest request,IReadOnlyList<PayslipDesignField> sourceFields,IReadOnlyList<PayslipDesignCode> codes)
    {
        var template=request.Template;
        if(template is null || template.ClientId<=0 || template.Id<0 || template.Theme!=NativePayslipDesignService.Theme
            || string.IsNullOrWhiteSpace(template.Name) || template.Name.Length>200 || template.Note is null || template.Note.Length>120
            || request.AmountDecimalPlaces is not (0 or 2) || request.Fields is null || request.Fields.Count is <1 or >100)
            throw new InvalidOperationException("Choose a client, Excel Payslip theme, supported precision and bounded presentation fields.");
        if(request.Fields.Any(field=>field is null || string.IsNullOrWhiteSpace(field.Label) || field.Label.Length>150)
            || request.Fields.Select(field=>field.SourceColumn).Distinct().Count()!=request.Fields.Count
            || request.Fields.Count!=sourceFields.Count
            || !request.Fields.OrderBy(f=>f.SourceColumn).Select(f=>(f.SourceColumn,f.Label,f.Kind))
                .SequenceEqual(sourceFields.OrderBy(f=>f.SourceColumn).Select(f=>(f.SourceColumn,f.Label,f.Kind))))
            throw new InvalidOperationException("Keep the selected source's field labels and kinds; use visibility and order to preview its layout.");
        foreach(var field in request.Fields)
        {
            if(field.NativeCode is null || field.InfoToken is null || field.NativeCode.Length>100 || field.InfoToken.Length>100)
                throw new InvalidOperationException("A preview mapping is invalid.");
            if(field.Kind=="info")
            {
                if(field.NativeCode.Length>0 || field.InfoToken.Length>0 && !NativePayslipMonthlyComponentInformation.SupportedToken(field.InfoToken))
                    throw new InvalidOperationException("Select a supported information field.");
                var original=sourceFields.Single(f=>f.SourceColumn==field.SourceColumn);
                if(field.SourceColumn>=1500 && field.InfoToken.Length>0 && field.InfoToken!=original.InfoToken)
                    throw new InvalidOperationException("Supplemental Master information keeps its verified field meaning.");
            }
            else if(NativePayslipDesignService.Category(field.Kind)=="" || field.InfoToken.Length>0
                || field.NativeCode.Length>0 && !codes.Any(code=>code.Code==field.NativeCode && NativePayslipDesignService.LineKind(code.Category)==field.Kind))
                throw new InvalidOperationException("A preview component mapping does not match its presentation section.");
        }
        NativePayslipMonthlyComponentInformation.ValidateCatalogue(request.Fields,codes);
    }

    internal static ExcelPayslipRow CreateRow(PayslipDesignSaveRequest request,IReadOnlyList<PayslipDesignField>? sourceFields=null)
    {
        var row=new ExcelPayslipRow { Id="sample-design",EmployeeCode="SAMPLE-001",EmployeeName="Sample Employee" };
        var amounts=request.Fields.Where(field=>field.Kind!="info").GroupBy(field=>field.Kind).SelectMany(group=>group.OrderBy(field=>field.SourceColumn)
            .Select((field,index)=>(field.SourceColumn,Amount:field.Kind switch { "earning"=>index==0?25000m:index==1?1200m:500m,"deduction"=>index==0?1800m:index==1?150m:100m,_=>index==0?1950m:300m })))
            .ToDictionary(value=>value.SourceColumn,value=>value.Amount);
        var gross=request.Fields.Where(field=>field.Kind=="earning").Sum(field=>amounts[field.SourceColumn]);
        var deductions=request.Fields.Where(field=>field.Kind=="deduction").Sum(field=>amounts[field.SourceColumn]);
        foreach(var field in request.Fields)
        {
            if(field.Kind=="info")
            {
                var token=field.InfoToken.Length>0?field.InfoToken:sourceFields?.SingleOrDefault(source=>source.SourceColumn==field.SourceColumn)?.InfoToken??"";
                if(!field.Visible || !request.Template.ShowBank && token is "BANK_ACCOUNT" or "BANK_NAME" or "IFSC")continue;
                row.Information.Add(new(){Label=field.Label,Value=NativePayslipMonthlyComponentInformation.TryCode(token,out _)?"Sample monthly amount":token=="SAVED_MONTHLY_GROSS"?gross.ToString(CultureInfo.InvariantCulture):token=="CURRENT_ANNUAL_CTC"?(gross*12).ToString(CultureInfo.InvariantCulture):Information(token)});
                continue;
            }
            var value=amounts[field.SourceColumn];
            if(!field.Visible)continue;
            var line=new ExcelPayslipAmount { Label=field.Label,Amount=value };
            (field.Kind=="earning"?row.Earnings:field.Kind=="deduction"?row.Deductions:row.EmployerContributions).Add(line);
        }
        row.DeclaredGross=gross;row.DeclaredDeductions=deductions;row.NetPay=gross-deductions;
        if(request.Template.ShowYtd)
            row.Information.AddRange(new[]{new ExcelPayslipText{Label="YTD earnings",Value=(gross*6).ToString(CultureInfo.InvariantCulture)},new ExcelPayslipText{Label="YTD deductions",Value=(deductions*6).ToString(CultureInfo.InvariantCulture)},new ExcelPayslipText{Label="YTD net pay",Value=((gross-deductions)*6).ToString(CultureInfo.InvariantCulture)}});
        return row;
    }

    private static string Information(string token)=>token switch
    {
        "DESIGNATION"=>"Sample designation","DEPARTMENT"=>"Administration","GRADE"=>"Staff","WORK_LOCATION"=>"Sample location",
        "JOINING_DATE"=>"01-01-2026","EMAIL"=>"employee@example.invalid","MOBILE" or "UAN" or "ESIC" or "PAN" or "AADHAAR" or "BANK_ACCOUNT" or "IFSC"=>"-",
        "BANK_NAME"=>"Sample Bank","ADDRESS"=>"Sample address","SAVED_MONTHLY_GROSS"=>"26200","CURRENT_ANNUAL_CTC"=>"314400",
        "PAYROLL_DAYS" or "PRESENT_DAYS" or "PAYABLE_DAYS"=>"30",_=>"Sample value"
    };
}
