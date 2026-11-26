using System.Data;
using Dapper;
using MySqlConnector;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed record EmployeeSaveResult(int Id, string EmployeeCode);

public partial class EmployeeRepository
{
    private static Task<EmployeeSaveResult> ReadSavedEmployeeCodeAsync(MySqlConnection db, MySqlTransaction tx, int id, int clientId) =>
        db.QuerySingleAsync<EmployeeSaveResult>("SELECT Id, EmployeeCode FROM employees WHERE Id=@id AND ClientId=@clientId", new { id, clientId }, tx);

    public async Task<EmployeeSaveResult> SaveEmployeeCodeAsync(int id, int clientId, string? expectedCode, string? proposedCode, string actor, string? changeReason)
    {
        var code = proposedCode?.Trim() ?? "";
        if (id <= 0 || clientId <= 0 || expectedCode is null || code.Length is 0 or > 50 || code.Any(char.IsControl))
            throw new InvalidOperationException("Choose an existing employee and provide the previous code and a new employee code of up to 50 characters.");
        var reason = changeReason?.Trim() ?? "";
        if (reason.Length > 500) throw new InvalidOperationException("Use up to 500 characters for the change reason.");

        await using var db = Connection();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        // Take locks in the same order as ordinary master edits and imports.
        await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@clientId FOR UPDATE", new { clientId }, tx);
        var original = await db.QuerySingleOrDefaultAsync<EmployeeSaveResult>(
            "SELECT Id, EmployeeCode FROM employees WHERE Id=@id AND ClientId=@clientId FOR UPDATE", new { id, clientId }, tx)
            ?? throw new KeyNotFoundException("Employee was not found in the selected client.");
        if (original.EmployeeCode == code)
        {
            await tx.CommitAsync();
            return original;
        }
        if (original.EmployeeCode != expectedCode)
            throw new DBConcurrencyException("The employee code has changed since this record was opened. Refresh the employee before saving.");

        const string duplicateMessage = "This employee code is already assigned to another employee in this client.";
        if (await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM employees WHERE ClientId=@clientId AND Id<>@id AND UPPER(TRIM(EmployeeCode))=UPPER(@code))", new { clientId, id, code }, tx))
            throw new DBConcurrencyException(duplicateMessage);

        var before = await LoadEmployeeAsync(db, id, tx) ?? throw new KeyNotFoundException("Employee no longer exists.");
        try
        {
            await db.ExecuteAsync("UPDATE employees SET EmployeeCode=@code WHERE Id=@id AND ClientId=@clientId", new { code, id, clientId }, tx);
        }
        catch (MySqlException error) when (error.Number == 1062)
        {
            throw new DBConcurrencyException(duplicateMessage, error);
        }
        var saved = await ReadSavedEmployeeCodeAsync(db, tx, id, clientId);
        if (saved.EmployeeCode != code) throw new InvalidOperationException("The database did not retain the requested employee code.");
        var after = CloneEmployee(before);
        after.EmployeeCode = saved.EmployeeCode;
        await WriteAuditRowsAsync(db, after, before, "Master Update", EmployeeEmploymentRules.Today, actor, ["0001"], tx);
        if (reason.Length > 0)
            await db.ExecuteAsync("""
                INSERT INTO employee_audit_trail (EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
                VALUES (@id,@code,'Master Update','0001','EmployeeCodeChangeReason','',@reason,@today,@actor)
                """, new { id, code, reason, today = EmployeeEmploymentRules.Today, actor }, tx);
        await RecruitmentEmployeeMasterSync.EmployeeSavedAsync(db, tx, before, after, actor);
        await tx.CommitAsync();
        return saved;
    }
}
