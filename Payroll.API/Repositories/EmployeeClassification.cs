namespace Payroll.API.Repositories;

// Organizational classifications supplement the existing employee JSON; no new columns.
internal static class EmployeeClassification
{
    public const string EmploymentType = "COALESCE(NULLIF(NULLIF(TRIM(CASE WHEN JSON_VALID(e.PersonalJson) THEN JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.employmentType')) END), 'null'), ''), 'Not specified')";
    public const string Category = "COALESCE(NULLIF(NULLIF(TRIM(CASE WHEN JSON_VALID(e.PersonalJson) THEN JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.skillCategory')) END), 'null'), ''), 'Not categorized')";
    public const string Columns = EmploymentType + " AS `Employee Type`, " + Category + " AS `Employee Category`";
    public const string Filter = " AND (@EmploymentType IS NULL OR " + EmploymentType + "=@EmploymentType) AND (@EmployeeCategory IS NULL OR " + Category + "=@EmployeeCategory)";
}
