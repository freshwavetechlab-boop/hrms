using System.Reflection;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class EmployeeClassificationTests
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    [Fact]
    public void ClassificationRoundTripsThroughExistingPersonalJson()
    {
        var store = typeof(EmployeeRepository).Assembly.GetType("Payroll.API.Repositories.PayrollDataTableStore")!;
        var personal = new EmployeePersonalDetails { EmploymentType = "Part time", SkillCategory = "Skilled", Mobile = "9999999999" };
        var json = (JsonObject)store.GetMethod("ToPersonalJson", StaticPrivate)!.Invoke(null, [personal])!;
        var read = (EmployeePersonalDetails)store.GetMethod("ToPersonalDetails", StaticPrivate, [typeof(JsonObject)])!.Invoke(null, [json])!;
        Assert.Equal(personal.EmploymentType, read.EmploymentType);
        Assert.Equal(personal.SkillCategory, read.SkillCategory);
        Assert.Equal(personal.Mobile, read.Mobile);
        var legacy = (EmployeePersonalDetails)store.GetMethod("ToPersonalDetails", StaticPrivate, [typeof(JsonObject)])!.Invoke(null, [new JsonObject { ["skillCategory"] = "Unskilled" }])!;
        Assert.Equal("", legacy.EmploymentType);
        Assert.Equal("Unskilled", legacy.SkillCategory);
    }

    [Fact]
    public void ImportPlansNewValuesOnceWithoutChangingAnotherClientsMasters()
    {
        var current = new Dictionary<string, HashSet<string>> { ["Department"] = new(StringComparer.OrdinalIgnoreCase) { "Finance" } };
        var otherClient = new Dictionary<string, HashSet<string>> { ["Department"] = new(StringComparer.OrdinalIgnoreCase) { "Operations" } };
        var errors = new List<string>();
        var validate = typeof(EmployeeRepository).GetMethod("ValidateMaster", StaticPrivate)!;
        foreach (var value in new[] { "Finance", " Field team ", "field team", "", new string('x', 101) })
            validate.Invoke(null, ["Department", value, current, errors, 2, "Department", "Employees"]);
        Assert.Equal(2, current["Department"].Count);
        Assert.Contains("Field team", current["Department"]);
        Assert.Single(errors);
        Assert.Single(otherClient["Department"]);
        Assert.DoesNotContain("Field team", otherClient["Department"]);
    }

    [Fact]
    public void NewLocationsArePlannedOnceAndExplicitIdsStayClientValidated()
    {
        var location = typeof(EmployeeRepository).GetNestedType("LocationRef", BindingFlags.NonPublic)!;
        var byId = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(int), location))!;
        var byName = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(List<>).MakeGenericType(location)), StringComparer.OrdinalIgnoreCase)!;
        var errors = new List<string>();
        var resolve = typeof(EmployeeRepository).GetMethod("ResolveWorkLocationId", StaticPrivate)!;
        object? Resolve(string id, string name) => resolve.Invoke(null, [id, name, byId, byName, errors, "Employees", 2]);
        var planned = Assert.IsType<int>(Resolve("", " Field office "));
        Assert.True(planned < 0);
        Assert.Equal(planned, Resolve("", "field office"));
        Assert.Null(Resolve("", ""));
        Assert.Empty(errors);
        Assert.Null(Resolve("999", "Field office"));
        Assert.Null(Resolve(planned.ToString(), "Field office"));
        Assert.Null(Resolve("", new string('x', 201)));
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void ImportAcceptsBothEmployeeTypeAndEmploymentTypeHeaders()
    {
        var headerMap = typeof(EmployeeRepository).GetMethod("HeaderMap", StaticPrivate)!;
        var expected = (Dictionary<string, int>)headerMap.Invoke(null, [new List<string> { "Employee Code", "Employee Type" }])!;
        var alias = (Dictionary<string, int>)headerMap.Invoke(null, [new List<string> { "Employee Code", "Employment Type" }])!;
        Assert.Equal(expected["employeetype"], alias["employeetype"]);
    }

    [Fact]
    public void ClassificationChangesAreAuditedAsOrganizationalAssignment()
    {
        var employee = new Employee { PersonalDetails = new() { EmploymentType = "Full Time", SkillCategory = "Skilled" } };
        var audit = (Dictionary<string, string>)typeof(EmployeeRepository).GetMethod("AuditValues", StaticPrivate)!.Invoke(null, [employee])!;
        Assert.Equal("Full Time", audit["0001:EmploymentType"]);
        Assert.Equal("Skilled", audit["0001:SkillCategory"]);
    }
}
