using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    internal const int MaximumJobDescriptionQualificationBytes = 65535;

    internal static string ValidateJobDescriptionQualifications(SaveRecruitmentJobDescriptionVersion request)
    {
        if (request.Qualifications is null) return "Qualification details are invalid. Reload the JD and try again.";
        for (var index = 0; index < request.Qualifications.Count; index++)
        {
            var qualification = request.Qualifications[index];
            if (qualification is null) return $"Qualification row {index + 1} is invalid. Review that row and try again.";
            if (Encoding.UTF8.GetByteCount((qualification.QualificationName ?? "").Trim()) > MaximumJobDescriptionQualificationBytes)
                return $"Qualification row {index + 1} is too long for one entry. Split the eligibility wording across qualification rows without removing its requirements.";
            if ((qualification.Specialization ?? "").Trim().EnumerateRunes().Count() > 180)
                return $"Qualification row {index + 1}: specialization must be 180 characters or fewer. Keep the full eligibility wording in the qualification field.";
        }
        return "";
    }

    /// <summary>
    /// Expands only the legacy qualification-name column. Call outside a transaction:
    /// MySQL ALTER TABLE commits implicitly. Wider text columns are never narrowed.
    /// This is also the narrow entry point for an explicit, reviewed schema upgrade.
    /// </summary>
    public static async Task EnsureJobDescriptionQualificationCapacityAsync(MySqlConnection db)
    {
        var column = await QualificationColumnAsync(db);
        if (column is null) throw new InvalidOperationException("JD qualification storage is not initialized. Contact support.");
        if (QualificationColumnIsWideEnough(column.DataType)) return;
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK('recruitment_jd_qualification_capacity_v1',10)") != 1)
            throw new InvalidOperationException("JD qualification storage is being updated. Please retry shortly.");
        try
        {
            column = await QualificationColumnAsync(db)
                ?? throw new InvalidOperationException("JD qualification storage is not initialized. Contact support.");
            if (QualificationColumnIsWideEnough(column.DataType)) return;
            if (!new[] { "varchar", "char", "tinytext" }.Contains(column.DataType, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("JD qualification storage has an unsupported column type. Contact support.");
            if (!SafeQualificationSchemaIdentifier(column.CharacterSet) || !SafeQualificationSchemaIdentifier(column.Collation))
                throw new InvalidOperationException("JD qualification storage has an invalid text encoding. Contact support.");
            if (column.DefaultValue is not null)
                throw new InvalidOperationException("JD qualification storage has a custom default. Contact support before changing its capacity.");
            var nullable = column.IsNullable.Equals("YES", StringComparison.OrdinalIgnoreCase) ? "NULL" : "NOT NULL";
            await db.ExecuteAsync($"ALTER TABLE recruitment_jd_qualification_requirements MODIFY COLUMN QualificationName TEXT CHARACTER SET {column.CharacterSet} COLLATE {column.Collation} {nullable} COMMENT @Comment", new { column.Comment });
        }
        finally { await db.ExecuteScalarAsync<int>("SELECT RELEASE_LOCK('recruitment_jd_qualification_capacity_v1')"); }
    }

    internal static bool QualificationColumnIsWideEnough(string dataType) => dataType.Equals("text", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("mediumtext", StringComparison.OrdinalIgnoreCase) || dataType.Equals("longtext", StringComparison.OrdinalIgnoreCase);

    private static bool SafeQualificationSchemaIdentifier(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static Task<QualificationColumn?> QualificationColumnAsync(MySqlConnection db) => db.QuerySingleOrDefaultAsync<QualificationColumn>(@"
SELECT DATA_TYPE DataType,CHARACTER_SET_NAME CharacterSet,COLLATION_NAME Collation,IS_NULLABLE IsNullable,
COLUMN_DEFAULT DefaultValue,COLUMN_COMMENT Comment FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_jd_qualification_requirements' AND COLUMN_NAME='QualificationName'");

    private sealed class QualificationColumn
    {
        public string DataType { get; set; } = "";
        public string CharacterSet { get; set; } = "";
        public string Collation { get; set; } = "";
        public string IsNullable { get; set; } = "";
        public string? DefaultValue { get; set; }
        public string Comment { get; set; } = "";
    }
}
