using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal static string PreviewIdentityKey(string field,string? value) => field switch {
        "Employee Code"=>NormalizeCode(value),
        "Phone"=>IsScientificBankAccount(value)?"":NormalizeMobile(value),
        "Bank Account"=>System.Text.RegularExpressions.Regex.IsMatch(value??"",@"^[0-9\s'-]+$")?NormalizeBankAccount(value):"",
        "UAN"=>System.Text.RegularExpressions.Regex.IsMatch((value??"").Trim(),@"^[0-9]{12}$")&&!IsPlaceholder((value??"").Trim())?(value??"").Trim():"",
        "Email"=>(value??"").Contains('@')?(value??"").Trim().ToLowerInvariant():"",
        _=>""
    };
    // The same pure identity rules feed recruitment, salary-preview and locked Apply.
    // Results expose masked evidence and employee codes, never raw identifier values.
    internal static List<EmployeeImportPreflightRow> BuildImportIdentityPreflightRows(
        EmployeeImportWorkbook workbook, string importMode, IReadOnlyList<Employee> existing, bool includeFieldChanges = true)
    {
        var byId = existing.ToDictionary(employee => employee.Id);
        var byCode = BuildIdentityMap(existing, employee => NormalizeCode(employee.EmployeeCode));
        var byEmail = BuildIdentityMap(existing, employee => employee.WorkEmail.Trim().ToLowerInvariant());
        var byMobile = BuildIdentityMap(existing, employee => NormalizeMobile(employee.PersonalDetails?.Mobile));
        var byAadhaar = BuildIdentityMap(existing, employee => NormalizeAadhaar(employee.PersonalDetails?.AadhaarNumber));
        var byPan = BuildIdentityMap(existing, employee => NormalizePan(employee.PersonalDetails?.PanNumber));
        var byBank = BuildIdentityMap(existing, employee => NormalizeBankAccount(employee.PaymentDetails?.BankAccountNo));

        var rows = GetEmployeeDataSheet(workbook);
        var sheetName = GetEmployeeDataSheetName(workbook);
        var map = rows.Count > 0 ? HeaderMap(rows[0]) : new Dictionary<string, int>();
        var resultRows = new List<EmployeeImportPreflightRow>();
        var workbookIdentifiers = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < rows.Count; index++)
        {
            var source = rows[index];
            if (Blank(source)) continue;
            var rowNumber = index + 1;
            var blocking = new List<string>();
            var reasons = new List<string>();
            var reviewCandidates = new Dictionary<int, (Employee Employee, List<string> Reasons)>();
            var hardConflict = false;
            var alternateIdentifierConflict = false;
            Employee? stableMatch = null;
            // Remain blocked for data review, but do not turn the same rounded
            // scientific text into evidence that unrelated people share a bank.
            if (IsScientificBankAccount(Cell(source, map, "Bank Account No")))
            {
                blocking.Add(UnreliableBankIdentityMessage);
                hardConflict = true;
            }

            var idText = Cell(source, map, "Employee ID");
            if (!string.IsNullOrWhiteSpace(idText))
            {
                if (!int.TryParse(idText, out var employeeId) || employeeId <= 0)
                {
                    blocking.Add("Employee ID must be a positive whole number.");
                    hardConflict = true;
                }
                else if (!byId.TryGetValue(employeeId, out stableMatch))
                {
                    blocking.Add($"Employee ID {employeeId} does not belong to this client.");
                    hardConflict = true;
                }
                else
                {
                    reasons.Add("Exact Employee ID");
                    AddReviewCandidate(reviewCandidates, stableMatch, "Exact Employee ID");
                }
            }

            var code = Cell(source, map, "Employee Code");
            var codeMatches = LookupIdentity(byCode, NormalizeCode(code));
            if (codeMatches.Count > 1)
            {
                blocking.Add($"Employee Code \"{code}\" is assigned to multiple employees.");
                hardConflict = true;
            }
            foreach (var employee in codeMatches) AddReviewCandidate(reviewCandidates, employee, "Exact Employee Code");
            var codeMatch = codeMatches.Count == 1 ? codeMatches[0] : null;
            if (stableMatch is not null && codeMatch is not null && stableMatch.Id != codeMatch.Id)
            {
                blocking.Add($"Employee ID and Employee Code \"{code}\" identify different employees.");
                hardConflict = true;
            }
            else if (stableMatch is not null && !string.IsNullOrWhiteSpace(code) && !string.Equals(stableMatch.EmployeeCode, code, StringComparison.OrdinalIgnoreCase))
            {
                blocking.Add($"Employee ID {stableMatch.Id} belongs to employee code \"{stableMatch.EmployeeCode}\", not \"{code}\".");
                hardConflict = true;
            }
            else if (stableMatch is null && codeMatch is not null)
            {
                stableMatch = codeMatch;
                reasons.Add("Exact Employee Code");
            }

            var identifierCandidates = new Dictionary<int, (Employee Employee, List<string> Reasons)>();
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byEmail, Cell(source, map, "Work Email").Trim().ToLowerInvariant()), "Work email");
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byMobile, NormalizeMobile(Cell(source, map, "Mobile"))), "Mobile");
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byAadhaar, NormalizeAadhaar(Cell(source, map, "Aadhaar"))), "Aadhaar");
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byPan, NormalizePan(Cell(source, map, "PAN"))), "PAN");
            AddIdentifierMatches(identifierCandidates, LookupIdentity(byBank, NormalizeBankAccount(Cell(source, map, "Bank Account No"))), "Bank Account");
            foreach (var candidate in identifierCandidates.Values)
                foreach (var reason in candidate.Reasons)
                    AddReviewCandidate(reviewCandidates, candidate.Employee, $"Exact {reason}");

            if (identifierCandidates.Count > 1)
            {
                var detail = string.Join("; ", identifierCandidates.Values.Select(candidate =>
                    $"{candidate.Employee.EmployeeCode}: {string.Join(", ", candidate.Reasons)}"));
                blocking.Add($"Identifiers point to different employees ({detail}).");
                alternateIdentifierConflict = true;
            }
            else if (identifierCandidates.Count == 1)
            {
                var identifierMatch = identifierCandidates.Values.Single();
                if (stableMatch is not null && stableMatch.Id != identifierMatch.Employee.Id)
                {
                    blocking.Add($"{string.Join(", ", identifierMatch.Reasons)} belongs to employee {identifierMatch.Employee.EmployeeCode}, not {stableMatch.EmployeeCode}.");
                    alternateIdentifierConflict = true;
                }
                else
                {
                    stableMatch ??= identifierMatch.Employee;
                    reasons.AddRange(identifierMatch.Reasons.Select(reason => $"Exact {reason}"));
                }
            }

            var matchStatus = stableMatch is null ? "New" : reasons.Any(reason => reason is "Exact Employee ID" or "Exact Employee Code")
                ? reasons.Contains("Exact Employee ID") ? "MatchedByEmployeeId" : "MatchedByEmployeeCode"
                : "MatchedByIdentifiers";

            if (blocking.Count == 0 && stableMatch is null)
            {
                var probable = FindNameAddressMatches(existing, source, map);
                if (probable.Count > 1)
                {
                    blocking.Add($"Name and address match multiple employees: {string.Join(", ", probable.Select(employee => employee.EmployeeCode))}.");
                    hardConflict = true;
                    foreach (var employee in probable) AddReviewCandidate(reviewCandidates, employee, "Same normalized name and address");
                }
                else if (probable.Count == 1)
                {
                    stableMatch = probable[0];
                    matchStatus = "ProbableDuplicate";
                    reasons.Add("Same normalized name and address; manual decision required");
                    AddReviewCandidate(reviewCandidates, stableMatch, "Same normalized name and address");
                }
            }

            if (importMode == InsertImportMode && stableMatch is not null && matchStatus != "ProbableDuplicate")
            {
                blocking.Add($"Employee {stableMatch.EmployeeCode} already exists. Insert mode accepts new employees only.");
                hardConflict = true;
            }
            if (importMode == UpdateImportMode && stableMatch is null)
            {
                blocking.Add("No existing employee was identified. Update mode accepts existing employees only.");
                if (!alternateIdentifierConflict) hardConflict = true;
            }
            if (stableMatch is null && string.IsNullOrWhiteSpace(code))
            {
                blocking.Add("Employee Code is required for a new employee. Generate or map a code before confirmation.");
                if (!alternateIdentifierConflict) hardConflict = true;
            }

            var canResolveConflict = alternateIdentifierConflict && !hardConflict && importMode != InsertImportMode && reviewCandidates.Count > 0;
            if (canResolveConflict) matchStatus = "IdentityConflict";
            else if (blocking.Count > 0) matchStatus = "Blocked";
            var displayedMatch = canResolveConflict ? null : stableMatch;
            var changes = !includeFieldChanges || displayedMatch is null ? [] : BuildImportChanges(source, map, displayedMatch);
            var candidateEmployees = reviewCandidates.Values.Select(candidate => new EmployeeImportCandidate(
                candidate.Employee.Id,
                candidate.Employee.EmployeeCode,
                $"{candidate.Employee.FirstName} {candidate.Employee.LastName}".Trim(),
                candidate.Reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                includeFieldChanges ? BuildImportChanges(source, map, candidate.Employee) : [])).ToList();
            var identityEvidence = includeFieldChanges ? BuildIdentityEvidence(source, map, byId, byCode, byMobile, byAadhaar, byPan, byBank) : [];
            resultRows.Add(new EmployeeImportPreflightRow(
                rowNumber,
                sheetName,
                displayedMatch?.EmployeeCode ?? code,
                matchStatus,
                displayedMatch?.Id,
                displayedMatch?.EmployeeCode,
                displayedMatch is null ? null : $"{displayedMatch.FirstName} {displayedMatch.LastName}".Trim(),
                reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                blocking.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                changes,
                candidateEmployees,
                identityEvidence,
                canResolveConflict));
            TrackWorkbookIdentifier(workbookIdentifiers, "Mobile", NormalizeMobile(Cell(source, map, "Mobile")), resultRows.Count - 1);
            TrackWorkbookIdentifier(workbookIdentifiers, "Work email", Cell(source, map, "Work Email").Trim().ToLowerInvariant(), resultRows.Count - 1);
            TrackWorkbookIdentifier(workbookIdentifiers, "Aadhaar", NormalizeAadhaar(Cell(source, map, "Aadhaar")), resultRows.Count - 1);
            TrackWorkbookIdentifier(workbookIdentifiers, "PAN", NormalizePan(Cell(source, map, "PAN")), resultRows.Count - 1);
            TrackWorkbookIdentifier(workbookIdentifiers, "Bank Account", NormalizeBankAccount(Cell(source, map, "Bank Account No")), resultRows.Count - 1);
        }

        foreach (var duplicate in workbookIdentifiers.Where(item => item.Value.Distinct().Count() > 1))
        {
            var separator = duplicate.Key.IndexOf(':');
            var label = separator > 0 ? duplicate.Key[..separator] : "Identifier";
            foreach (var rowIndex in duplicate.Value.Distinct())
            {
                var row = resultRows[rowIndex];
                var otherRows = duplicate.Value.Distinct().Where(index => index != rowIndex).Select(index => resultRows[index].RowNumber);
                var blockingReasons = row.BlockingReasons
                    .Append($"{label} is also supplied on workbook row(s) {string.Join(", ", otherRows)}. One person must not be inserted or updated twice.")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                resultRows[rowIndex] = row with { MatchStatus = "Blocked", BlockingReasons = blockingReasons, CanResolveConflict = false };
            }
        }

        return resultRows;
    }
}
