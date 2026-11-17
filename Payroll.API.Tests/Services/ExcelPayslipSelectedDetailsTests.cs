using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipSelectedDetailsTests
{
    private static ExcelPayslipRepository.MasterSyncBinding Binding(string rowId,int employeeId,int sourceRow=2) => new(
        new(){Id=rowId,SourceRow=sourceRow,EmployeeCode="SYN-"+employeeId,EmployeeName="Synthetic employee",NetPay=1234.567m,
            Earnings=[new(){Label="Basic",Amount=1234.567m}]},
        new(rowId,employeeId,sourceRow,"identity","salary",30,30,30,false),new(),new(),new(),null);
    private static Dictionary<string,List<ExcelPayslipRepository.MasterSyncBinding>> Group(params ExcelPayslipRepository.MasterSyncBinding[] values)
        => values.GroupBy(value=>value.Row.Id).ToDictionary(group=>group.Key,group=>group.ToList(),StringComparer.Ordinal);

    [Fact]
    public void Full_read_retains_all_unique_bindings_and_selected_pdf_scopes_only_master_salary_details()
    {
        var bindings=Group(Binding("first",1),Binding("second",2,3));var before=JsonSerializer.Serialize(bindings);
        Assert.Equal(2,ExcelPayslipRepository.SelectMasterDetailBindings(bindings,null).Length);
        var selected=Assert.Single(ExcelPayslipRepository.SelectMasterDetailBindings(bindings,new HashSet<string>(["second"])));
        Assert.Equal("second",selected.Row.Id);Assert.Equal(2,selected.Link.EmployeeId);
        Assert.Equal(before,JsonSerializer.Serialize(bindings));
    }

    [Fact]
    public void Selecting_one_duplicate_employee_row_cannot_hide_another_authoritative_source_row()
    {
        var bindings=Group(Binding("first",1),Binding("duplicate",1,3),Binding("safe",2,4));
        Assert.Empty(ExcelPayslipRepository.SelectMasterDetailBindings(bindings,new HashSet<string>(["first"])));
        Assert.Equal("safe",Assert.Single(ExcelPayslipRepository.SelectMasterDetailBindings(bindings,null)).Row.Id);
    }

    [Fact]
    public void Contradictory_receipt_owners_still_fail_before_selection_and_repeated_same_owner_preserves_latest_binding()
    {
        var old=Binding("safe",3,4);var latest=old with {Link=old.Link with {ImportId="latest"}};
        var bindings=Group(Binding("ambiguous",1),Binding("ambiguous",2),latest,old);
        Assert.Empty(ExcelPayslipRepository.SelectMasterDetailBindings(bindings,new HashSet<string>(["ambiguous"])));
        Assert.Same(latest,Assert.Single(ExcelPayslipRepository.SelectMasterDetailBindings(bindings,new HashSet<string>(["safe"]))));
    }

    [Fact]
    public void Unknown_or_empty_selected_set_does_no_salary_work_and_source_membership_guard_still_rejects_foreign_row()
    {
        var binding=Binding("first",1);var bindings=Group(binding);
        Assert.Empty(ExcelPayslipRepository.SelectMasterDetailBindings(bindings,new HashSet<string>()));
        Assert.Empty(ExcelPayslipRepository.SelectMasterDetailBindings(bindings,new HashSet<string>(["foreign"])));
        var batch=new ExcelPayslipBatch {Rows=[binding.Row]};
        var (rows,error)=ExcelPayslipRepository.SelectRows(batch,new(){RowIds=["foreign"],AmountDecimalPlaces=0});
        Assert.Empty(rows);Assert.Equal("A selected row does not belong to this saved batch.",error);
    }

    [Fact]
    public void Unselected_current_salary_review_does_not_change_selected_original_amounts_or_export_eligibility()
    {
        var selected=Binding("selected",1).Row;var other=Binding("other",2,3).Row;
        other.MasterDetails=new(2,"Synthetic role"){Linked=true,Salary=ExcelPayslipRepository.SyncReview("Review other employee")};
        other.Employment=new(2,"SYN-2","Active",null,false,false);
        var batch=new ExcelPayslipBatch{Rows=[selected,other]};var before=JsonSerializer.Serialize(batch);
        var result=ExcelPayslipRepository.SelectRows(batch,new(){RowIds=["selected"],AmountDecimalPlaces=2});
        Assert.Null(result.Error);Assert.Equal(1234.567m,Assert.Single(result.Rows).NetPay);
        Assert.Equal(before,JsonSerializer.Serialize(batch));
        Assert.NotNull(ExcelPayslipRepository.SelectRows(batch,new(){RowIds=["other"],AmountDecimalPlaces=2}).Error);
    }

    [Theory]
    [InlineData("empty")][InlineData("duplicate")][InlineData("blank")][InlineData("null")][InlineData("too-many")][InlineData("precision")]
    public void Pdf_preflight_and_final_selection_keep_the_same_existing_validation_errors(string kind)
    {
        var request=new ExcelPayslipSelection{RowIds=["valid"],AmountDecimalPlaces=0};
        switch(kind)
        {
            case "empty":request.RowIds=[];break;
            case "duplicate":request.RowIds=["valid","valid"];break;
            case "blank":request.RowIds=[" "];break;
            case "null":request.RowIds=null!;break;
            case "too-many":request.RowIds=Enumerable.Range(0,1001).Select(value=>"row"+value).ToList();break;
            case "precision":request.AmountDecimalPlaces=1;break;
        }
        Assert.Equal(ExcelPayslipRepository.SelectionError(request),ExcelPayslipRepository.SelectRows(new(),request).Error);
        Assert.NotNull(ExcelPayslipRepository.SelectionError(request));
    }
}
