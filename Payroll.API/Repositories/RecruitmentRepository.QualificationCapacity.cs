using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public partial class RecruitmentRepository
{
    /// <summary>
    /// Widens only the legacy requisition qualification column. Call on an open
    /// connection outside any transaction: MySQL ALTER TABLE commits implicitly.
    /// The narrow migrator entry point never seeds data or rewrites request rows.
    /// </summary>
    public static async Task EnsureRequisitionQualificationCapacityAsync(MySqlConnection db)
    {
        var column = await RequisitionQualificationColumnAsync(db)
            ?? throw new InvalidOperationException("Hiring-request qualification storage is not initialized. Run the normal schema migrator first.");
        if (RequisitionQualificationIsWideEnough(column.DataType)) return;
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK('recruitment_rfr_qualification_capacity_v1',10)") != 1)
            throw new InvalidOperationException("Hiring-request qualification storage is being updated. Please retry shortly.");
        try
        {
            column = await RequisitionQualificationColumnAsync(db)
                ?? throw new InvalidOperationException("Hiring-request qualification storage is not initialized. Run the normal schema migrator first.");
            if (RequisitionQualificationIsWideEnough(column.DataType)) return;
            if (!new[] { "varchar", "char", "tinytext" }.Contains(column.DataType, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Hiring-request qualification storage has an unsupported column type. Review its schema before widening it.");
            if (!Regex.IsMatch(column.CharacterSet, "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)
                || !Regex.IsMatch(column.Collation, "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Hiring-request qualification storage has an invalid text encoding. Review its schema before widening it.");
            if (!string.IsNullOrWhiteSpace(column.Extra) || !string.IsNullOrWhiteSpace(column.GenerationExpression))
                throw new InvalidOperationException("Hiring-request qualification storage has custom generated attributes. Review its schema before widening it.");
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_requisitions' AND COLUMN_NAME='Qualification'") != 0)
                throw new InvalidOperationException("Hiring-request qualification storage has a custom index. Review its schema before widening it.");
            var nullable = column.IsNullable.Equals("YES", StringComparison.OrdinalIgnoreCase) ? "NULL" : "NOT NULL";
            // MySQL 8 TEXT defaults require expression syntax, including the
            // existing empty-string default. Bind values; never interpolate SQL.
            var defaultSql = column.DefaultValue is not null ? " DEFAULT (@DefaultValue)" : nullable == "NULL" ? " DEFAULT NULL" : "";
            await db.ExecuteAsync($"ALTER TABLE recruitment_requisitions MODIFY COLUMN Qualification TEXT CHARACTER SET {column.CharacterSet} COLLATE {column.Collation} {nullable}{defaultSql} COMMENT @Comment",
                new { column.DefaultValue, column.Comment });
        }
        finally { await db.ExecuteScalarAsync<int>("SELECT RELEASE_LOCK('recruitment_rfr_qualification_capacity_v1')"); }
    }

    private static bool RequisitionQualificationIsWideEnough(string dataType) => dataType.Equals("text", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("mediumtext", StringComparison.OrdinalIgnoreCase) || dataType.Equals("longtext", StringComparison.OrdinalIgnoreCase);

    private static Task<RequisitionQualificationColumn?> RequisitionQualificationColumnAsync(MySqlConnection db) => db.QuerySingleOrDefaultAsync<RequisitionQualificationColumn>(@"
SELECT DATA_TYPE DataType,CHARACTER_SET_NAME CharacterSet,COLLATION_NAME Collation,IS_NULLABLE IsNullable,
COLUMN_DEFAULT DefaultValue,COLUMN_COMMENT Comment,EXTRA Extra,GENERATION_EXPRESSION GenerationExpression
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_requisitions' AND COLUMN_NAME='Qualification'");

    private sealed class RequisitionQualificationColumn
    {
        public string DataType { get; set; } = "";
        public string CharacterSet { get; set; } = "";
        public string Collation { get; set; } = "";
        public string IsNullable { get; set; } = "";
        public string? DefaultValue { get; set; }
        public string Comment { get; set; } = "";
        public string Extra { get; set; } = "";
        public string GenerationExpression { get; set; } = "";
    }
}
