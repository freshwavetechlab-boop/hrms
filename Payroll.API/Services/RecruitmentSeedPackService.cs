using Dapper;
using ExcelDataReader;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using System.Globalization;
using System.Text;

namespace Payroll.API.Services;

public sealed class RecruitmentSeedPackService(
    IConfiguration configuration,
    RecruitmentRepository requisitions,
    RecruitmentPipelineRepository pipelines,
    RecruitmentTalentRepository talent,
    WorkflowRepository workflows,
    RecruitmentPipelineActionService pipelineActions,
    RecruitmentCandidateActionRepository candidateActions,
    ResumeParsingService resumeParser)
{
    private const int MaximumWorkbookBytes = 10 * 1024 * 1024;
    private const int MaximumResumeFiles = 50;

    public async Task<(RecruitmentSeedPackImportResult? Result, string Error)> ImportAsync(
        IFormFile workbookFile,
        IReadOnlyList<IFormFile> resumeFiles,
        AuthUser user,
        string ipAddress,
        string userAgent,
        CancellationToken cancellationToken)
    {
        if (workbookFile is null || workbookFile.Length == 0) return (null, "Select the filled recruitment seed workbook.");
        if (workbookFile.Length > MaximumWorkbookBytes) return (null, "The seed workbook cannot exceed 10 MB.");
        if (!new[] { ".xlsx", ".xls" }.Contains(Path.GetExtension(workbookFile.FileName), StringComparer.OrdinalIgnoreCase))
            return (null, "Upload the .xlsx or .xls recruitment seed template.");
        if (resumeFiles.Count > MaximumResumeFiles) return (null, $"A maximum of {MaximumResumeFiles} resumes can be included in one seed pack.");

        Workbook workbook;
        try
        {
            workbook = await ReadWorkbookAsync(workbookFile, cancellationToken);
        }
        catch (Exception exception)
        {
            return (null, $"The recruitment workbook could not be read: {exception.Message}");
        }

        var hiringRows = workbook.Rows("Hiring Requests").Where(IsActionable).ToList();
        var jdRows = workbook.Rows("JD Overview").Where(IsActionable).ToList();
        var candidateRows = workbook.Rows("Candidates").Where(IsActionable).ToList();
        if (hiringRows.Count == 0 && candidateRows.Count == 0)
            return (null, "No import rows were found. Replace the EXAMPLE source keys with your own values before upload.");

        var structureError = ValidateStructure(workbook, hiringRows, jdRows, candidateRows);
        if (structureError.Length > 0) return (null, structureError);

        var duplicateError = DuplicateKeyError(hiringRows, "Source Key", "Hiring Requests")
            ?? DuplicateKeyError(jdRows, "Source Key", "JD Overview")
            ?? DuplicateKeyError(candidateRows, "Candidate Key", "Candidates");
        if (duplicateError is not null) return (null, duplicateError);

        var result = new RecruitmentSeedPackImportResult();
        var requestByKey = new Dictionary<string, RecruitmentRequisition>(StringComparer.OrdinalIgnoreCase);
        var jdByKey = jdRows.ToDictionary(row => row.Value("Source Key"), StringComparer.OrdinalIgnoreCase);

        foreach (var row in hiringRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ImportHiringRowAsync(workbook, row, jdByKey.GetValueOrDefault(row.Value("Source Key")), user, requestByKey, result);
        }

        var resumes = resumeFiles
            .Where(file => file is not null && file.Length > 0)
            .GroupBy(file => Path.GetFileName(file.FileName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in candidateRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ImportCandidateRowAsync(row, resumes, user, ipAddress, userAgent, result, cancellationToken);
        }

        result.TotalRows = result.Items.Count;
        result.Created = result.Items.Count(item => item.Outcome == "Created");
        result.Updated = result.Items.Count(item => item.Outcome == "Updated");
        result.Reused = result.Items.Count(item => item.Outcome == "Reused");
        result.Deferred = result.Items.Count(item => item.Outcome == "Deferred");
        result.Failed = result.Items.Count(item => item.Outcome == "Failed");
        return (result, "");
    }

    private async Task ImportHiringRowAsync(
        Workbook workbook,
        SheetRow row,
        SheetRow? jdRow,
        AuthUser user,
        IDictionary<string, RecruitmentRequisition> requestByKey,
        RecruitmentSeedPackImportResult result)
    {
        var key = row.Value("Source Key");
        try
        {
            var client = await ResolveClientAsync(row.Value("Client Code"), user);
            if (client.Id <= 0) throw new ImportValidationException($"Client code '{row.Value("Client Code")}' was not found in your access scope.");
            var requesterId = await ResolveRequesterAsync(client.Id, row.Value("Requester Employee Code"));
            if (requesterId <= 0) throw new ImportValidationException($"Active requester '{row.Value("Requester Employee Code")}' was not found for {client.Code}.");

            var marker = $"[IMPORT:{key}]";
            var existing = (await requisitions.SearchAsync(new RecruitmentSearchRequest { ClientId = client.Id }, user))
                .SingleOrDefault(item => item.SourceNotes.Contains(marker, StringComparison.OrdinalIgnoreCase));
            var mutable = existing is null || existing.Status is "Draft" or "Sent Back";
            RecruitmentRequisition request;
            var requestOutcome = existing is null ? "Created" : mutable ? "Updated" : "Reused";
            if (!mutable)
            {
                request = existing!;
            }
            else
            {
                var saved = await requisitions.SaveDraftAsync(BuildRequest(row, client.Id, requesterId, marker, existing), user);
                if (saved.Row is null) throw new ImportValidationException(saved.Error);
                request = saved.Row;
            }
            requestByKey[key] = request;
            result.Items.Add(Item(row, key, "Hiring request", requestOutcome,
                requestOutcome == "Reused" ? $"{request.RfrNumber} is {request.Status}; existing business state was preserved." : $"{request.RfrNumber} saved as {request.Status}.",
                requestId: request.Id));

            RecruitmentJobDescriptionVersion? jd = null;
            if (jdRow is not null)
            {
                var responsibilities = workbook.Rows("JD Responsibilities")
                    .Where(item => IsActionable(item) && item.Value("Source Key").Equals(key, StringComparison.OrdinalIgnoreCase))
                    .Select((item, index) => new RecruitmentJdResponsibility { ResponsibilityText = item.Value("Responsibility"), DisplayOrder = item.Int("Order", (index + 1) * 10) })
                    .Where(item => item.ResponsibilityText.Length > 0).ToList();
                var skills = workbook.Rows("ATS Skills")
                    .Where(item => IsActionable(item) && item.Value("Source Key").Equals(key, StringComparison.OrdinalIgnoreCase))
                    .Select((item, index) => new RecruitmentJdSkillRequirement
                    {
                        SkillName = item.Value("Skill"), IsRequired = item.Yes("Must Have"), MinimumYears = item.Decimal("Minimum Years"),
                        MinimumProficiency = item.Value("Proficiency"), WeightPercent = item.Decimal("Weight Percent"), DisplayOrder = item.Int("Order", (index + 1) * 10)
                    }).Where(item => item.SkillName.Length > 0).ToList();
                if (responsibilities.Count == 0) throw new ImportValidationException("Add at least one matching row in JD Responsibilities.");
                if (skills.Count == 0 || skills.All(skill => !skill.IsRequired)) throw new ImportValidationException("Add ATS skills and mark at least one skill as Must Have.");

                var versions = (await pipelines.GetJobDescriptionVersionsAsync(request.Id, user)).ToList();
                var immutable = versions.FirstOrDefault(version => version.Status is "Approved" or "Pending Approval" or "Published");
                var draft = versions.FirstOrDefault(version => version.Status is "Draft" or "Sent Back");
                var jdOutcome = immutable is not null ? "Reused" : draft is null ? "Created" : "Updated";
                if (immutable is not null)
                {
                    jd = immutable;
                }
                else
                {
                    var save = await pipelines.SaveJobDescriptionVersionAsync(BuildJobDescription(jdRow, request.Id, draft?.Id ?? 0, responsibilities, skills), user);
                    if (save.Row is null) throw new ImportValidationException(save.Error);
                    jd = save.Row;
                }
                result.Items.Add(Item(jdRow, key, "Job description + ATS", jdOutcome,
                    jdOutcome == "Reused" ? $"JD v{jd.VersionNumber} is {jd.Status}; existing state was preserved." : $"JD v{jd.VersionNumber} saved with {skills.Count} ATS skills.",
                    requestId: request.Id, jdId: jd.Id));
            }

            if (row.Action("Request Action") == "SUBMIT" && request.Status is ("Draft" or "Sent Back"))
            {
                var submitted = await requisitions.SubmitAsync(request.Id, user, workflows);
                if (submitted.Row is null) throw new ImportValidationException(submitted.Error);
                request = submitted.Row;
                requestByKey[key] = request;
                result.Items.Add(Item(row, key, "Hiring request workflow", "Updated", $"Request moved to {request.Status}.", requestId: request.Id, jdId: jd?.Id));
            }

            if (jd is not null && jdRow is not null && jd.Status is ("Draft" or "Sent Back"))
            {
                var jdAction = jdRow.Action("JD Action");
                if (jdAction == "APPROVE")
                {
                    if (!user.Permissions.Contains("settings.manage", StringComparer.OrdinalIgnoreCase))
                        throw new ImportValidationException("Direct JD approval requires settings.manage permission. Use Submit for the configured approval workflow.");
                    var approved = await pipelines.ApproveJobDescriptionDirectlyAsync(jd.Id, user);
                    if (approved.Row is null) throw new ImportValidationException(approved.Error);
                    result.Items.Add(Item(jdRow, key, "JD workflow", "Updated", "JD approved and bound to the open position.", requestId: request.Id, jdId: approved.Row.Id));
                }
                else if (jdAction == "SUBMIT")
                {
                    if (request.Status != "Approved")
                    {
                        result.Items.Add(Item(jdRow, key, "JD workflow", "Deferred", $"Request is {request.Status}; approve it before JD submission.", requestId: request.Id, jdId: jd.Id));
                    }
                    else
                    {
                        var workflowId = await workflows.GetDefaultIdAsync("RecruitmentJobDescription", request.ClientId);
                        if (!workflowId.HasValue)
                            result.Items.Add(Item(jdRow, key, "JD workflow", "Deferred", "No active default JD approval workflow is configured.", requestId: request.Id, jdId: jd.Id));
                        else
                        {
                            var submittedJd = await pipelines.SubmitJobDescriptionForApprovalAsync(jd.Id, workflowId.Value, user);
                            if (submittedJd.Row is null) throw new ImportValidationException(submittedJd.Error);
                            result.Items.Add(Item(jdRow, key, "JD workflow", "Updated", $"JD moved to {submittedJd.Row.Status}.", requestId: request.Id, jdId: jd.Id));
                        }
                    }
                }
            }
        }
        catch (ImportValidationException exception)
        {
            result.Items.Add(Item(row, key, "Hiring seed", "Failed", exception.Message));
        }
        catch
        {
            result.Items.Add(Item(row, key, "Hiring seed", "Failed", "This row could not be imported. Review its values and retry; other rows continued."));
        }
    }

    private async Task ImportCandidateRowAsync(
        SheetRow row,
        IReadOnlyDictionary<string, List<IFormFile>> resumes,
        AuthUser user,
        string ipAddress,
        string userAgent,
        RecruitmentSeedPackImportResult result,
        CancellationToken cancellationToken)
    {
        var key = row.Value("Candidate Key");
        try
        {
            var client = await ResolveClientAsync(row.Value("Client Code"), user);
            if (client.Id <= 0) throw new ImportValidationException($"Client code '{row.Value("Client Code")}' was not found in your access scope.");
            var position = await ResolvePositionAsync(client.Id, row.Value("Position Code"), user);
            if (position is null)
            {
                result.Items.Add(Item(row, key, "Candidate + ATS", "Deferred", "Approved open position/JD was not found. Approve the request and JD, then upload this same pack again."));
                return;
            }

            var fileName = Path.GetFileName(row.Value("Resume File"));
            if (!resumes.TryGetValue(fileName, out var matchingFiles) || matchingFiles.Count != 1)
                throw new ImportValidationException(matchingFiles is null ? $"Resume '{fileName}' was not included with the workbook." : $"Resume filename '{fileName}' was included more than once.");
            var file = matchingFiles[0];
            var parsed = await resumeParser.ParseAsync(file, cancellationToken);
            if (!parsed.Status.Equals("Parsed", StringComparison.OrdinalIgnoreCase))
                throw new ImportValidationException(string.IsNullOrWhiteSpace(parsed.Error) ? "Resume needs manual review before candidate import." : parsed.Error);
            var expectedEmail = NormalizeEmail(row.Value("Expected Email"));
            var expectedPhone = NormalizePhone(row.Value("Expected Phone"));
            if (expectedEmail.Length == 0 && expectedPhone.Length == 0)
                throw new ImportValidationException("Expected Email or Expected Phone is required to protect candidate identity.");
            if (expectedEmail.Length > 0 && !expectedEmail.Equals(NormalizeEmail(parsed.Facts.Email), StringComparison.OrdinalIgnoreCase))
                throw new ImportValidationException($"Resume email '{parsed.Facts.Email}' does not match Expected Email.");
            if (expectedPhone.Length > 0 && expectedPhone != NormalizePhone(parsed.Facts.Phone))
                throw new ImportValidationException($"Resume phone '{parsed.Facts.Phone}' does not match Expected Phone.");

            var candidateSearch = expectedEmail.Length > 0 ? expectedEmail : expectedPhone;
            var existingCandidate = (await talent.SearchCandidatesAsync(user, client.Id, candidateSearch, ""))
                .SingleOrDefault(candidate => (expectedEmail.Length > 0 && NormalizeEmail(candidate.Email) == expectedEmail)
                    || (expectedPhone.Length > 0 && NormalizePhone(candidate.Phone) == expectedPhone));
            var applicationExisted = existingCandidate is not null
                && (await talent.GetApplicationsAsync(user, position.Id, existingCandidate.Id, "")).Any();

            var intake = await talent.IntakeResumesAsync(new RecruitmentResumeIntakeRequest
            {
                ClientId = client.Id,
                PositionId = position.Id,
                SourceType = row.Value("Source Type", "Structured seed pack"),
                Files = [file]
            }, user, ipAddress, userAgent, cancellationToken);
            var imported = intake.Items.SingleOrDefault();
            if (imported?.Success != true || imported.Application is null || imported.Candidate is null)
                throw new ImportValidationException(imported?.Error ?? "Candidate resume could not be imported.");

            var applicationId = imported.Application.Id;
            var (pipelineId, pipelineError) = await pipelines.EnsureApplicationPipelineAsync(applicationId, user);
            if (pipelineId.HasValue)
            {
                var entry = await pipelineActions.ExecuteAsync(applicationId, "OnEntry", user);
                if (!entry.Executions.Any(execution => execution.ActionCode == "GENERATE_ACTION_LINK"))
                    await candidateActions.EnsureForCurrentStageAsync(applicationId, user);
            }
            var application = (await talent.GetApplicationsAsync(user, position.Id, imported.Candidate.Id, ""))
                .FirstOrDefault(item => item.Id == applicationId) ?? imported.Application;
            var outcome = applicationExisted ? "Reused" : "Created";
            var message = application.AtsScore.HasValue
                ? $"Candidate linked to {position.PositionCode}; ATS score {application.AtsScore:0.##}."
                : $"Candidate linked to {position.PositionCode}; ATS is queued or needs review.{(pipelineError.Length > 0 ? $" {pipelineError}" : "")}";
            result.Items.Add(Item(row, key, "Candidate + ATS", outcome, message, candidateId: imported.Candidate.Id, applicationId: application.Id, atsScore: application.AtsScore));
        }
        catch (ImportValidationException exception)
        {
            result.Items.Add(Item(row, key, "Candidate + ATS", "Failed", exception.Message));
        }
        catch
        {
            result.Items.Add(Item(row, key, "Candidate + ATS", "Failed", "Candidate import failed safely; the remaining rows continued."));
        }
    }

    private static SaveRecruitmentRequisition BuildRequest(SheetRow row, int clientId, int requesterId, string marker, RecruitmentRequisition? existing)
    {
        var notes = row.Value("Source Notes");
        if (!notes.Contains(marker, StringComparison.OrdinalIgnoreCase)) notes = string.Join(Environment.NewLine, new[] { notes, marker }.Where(value => value.Length > 0));
        return new SaveRecruitmentRequisition
        {
            Id = existing?.Id ?? 0,
            ClientId = clientId,
            RequestedByEmployeeId = existing?.RequestedByEmployeeId ?? requesterId,
            RequestDate = row.Date("Request Date") ?? DateTime.Today,
            PositionTitle = row.Required("Position Title"),
            Department = row.Required("Department"),
            NumberOfOpenings = Math.Max(1, row.Int("Openings", 1)),
            HiringType = row.Value("Hiring Type"), EmploymentType = row.Value("Employment Type", "Permanent"), PositionCategory = row.Value("Position Category"),
            HiringPriority = row.Value("Priority", "Normal"), JobLocation = row.Value("Work Location"), WorkMode = row.Value("Work Mode", "Office"),
            TargetJoiningDate = row.Date("Target Joining Date"), Project = row.Value("Project"), BusinessUnit = row.Value("Business Unit"), CostCenter = row.Value("Cost Center"),
            BudgetAvailable = row.Yes("Budget Available"), BudgetAmount = row.Decimal("Budget Amount"), Currency = row.Value("Currency", "INR"),
            CtcFlexibilityPercent = row.NullableDecimal("CTC Flexibility Percent"), ExperienceRange = row.Value("Experience"), Qualification = row.Value("Qualification"),
            RequiredSkills = row.Value("Required Skills"), PreferredSkills = row.Value("Preferred Skills"), Certifications = row.Value("Certifications"),
            Languages = row.Value("Languages"), Benefits = row.Value("Benefits"), BusinessJustification = row.Value("Business Justification"),
            ReasonForHiring = row.Value("Hiring Notes"), ExternalPositionCode = row.Value("External Position Code"), SourceType = row.Value("Source Type", "Structured seed pack"),
            SourceReference = row.Value("Source Reference", row.Value("Source Key")), SourceDocumentName = row.Value("Source Document"),
            SourceDocumentDate = row.Date("Source Document Date"), SourceAuthority = row.Value("Source Authority"), ExternalApprovalStatus = row.Value("Client Approval State"), SourceNotes = notes
        };
    }

    private static SaveRecruitmentJobDescriptionVersion BuildJobDescription(SheetRow row, long requestId, long id, List<RecruitmentJdResponsibility> responsibilities, List<RecruitmentJdSkillRequirement> skills) => new()
    {
        Id = id, RequisitionId = requestId, Title = row.Required("Title"), Summary = row.Required("Summary"), RolePurpose = row.Value("Role Purpose"),
        Responsibilities = responsibilities, Skills = skills,
        Qualifications = Split(row.Value("Qualifications")).Select((value, index) => new RecruitmentJdQualificationRequirement { QualificationName = value, IsMandatory = true, DisplayOrder = (index + 1) * 10 }).ToList(),
        Certifications = Split(row.Value("Certifications")).Select((value, index) => new RecruitmentJdCertificationRequirement { CertificationName = value, IsMandatory = false, DisplayOrder = (index + 1) * 10 }).ToList(),
        Languages = Split(row.Value("Languages")).Select((value, index) => new RecruitmentJdLanguageRequirement { LanguageName = value, DisplayOrder = (index + 1) * 10 }).ToList(),
        Benefits = Split(row.Value("Benefits")).Select((value, index) => new RecruitmentJdBenefit { BenefitName = value, DisplayOrder = (index + 1) * 10 }).ToList()
    };

    private static string ValidateStructure(Workbook workbook, IReadOnlyCollection<SheetRow> hiringRows, IReadOnlyCollection<SheetRow> jdRows, IReadOnlyCollection<SheetRow> candidateRows)
    {
        if (hiringRows.Count > 0)
        {
            var error = workbook.Require("Hiring Requests", "Source Key", "Client Code", "Requester Employee Code", "Position Title", "Department", "Openings", "Request Action")
                ?? workbook.Require("JD Overview", "Source Key", "Title", "Summary", "JD Action")
                ?? workbook.Require("JD Responsibilities", "Source Key", "Responsibility")
                ?? workbook.Require("ATS Skills", "Source Key", "Skill", "Must Have", "Minimum Years", "Weight Percent");
            if (error is not null) return error;
            var missingJd = hiringRows.Select(row => row.Value("Source Key")).FirstOrDefault(key => jdRows.All(row => !row.Value("Source Key").Equals(key, StringComparison.OrdinalIgnoreCase)));
            if (missingJd is not null) return $"JD Overview is missing Source Key '{missingJd}'.";
        }
        if (candidateRows.Count > 0)
        {
            var error = workbook.Require("Candidates", "Candidate Key", "Client Code", "Position Code", "Resume File", "Expected Email", "Expected Phone", "Source Type");
            if (error is not null) return error;
        }
        return "";
    }

    private async Task<(int Id, string Code)> ResolveClientAsync(string code, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryFirstOrDefaultAsync<(int Id, string Code)>("SELECT Id,Code FROM clients WHERE UPPER(Code)=UPPER(@Code) AND IsActive=TRUE AND (@ClientId IS NULL OR Id=@ClientId)", new { Code = code.Trim(), user.ClientId });
    }

    private async Task<int> ResolveRequesterAsync(int clientId, string employeeCode)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.ExecuteScalarAsync<int?>("SELECT Id FROM employees WHERE ClientId=@ClientId AND UPPER(EmployeeCode)=UPPER(@EmployeeCode) AND IsActive=TRUE LIMIT 1", new { ClientId = clientId, EmployeeCode = employeeCode.Trim() }) ?? 0;
    }

    private async Task<RecruitmentOpenPosition?> ResolvePositionAsync(int clientId, string positionCode, AuthUser user)
    {
        var positions = await requisitions.OpenPositionsAsync(user, clientId);
        return positions.SingleOrDefault(position => position.PositionCode.Equals(positionCode.Trim(), StringComparison.OrdinalIgnoreCase)
            && position.Status is not ("Closed" or "Cancelled" or "Filled") && position.LatestJobDescriptionVersionId is > 0 && position.JobDescriptionStatus == "Approved");
    }

    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    private static RecruitmentSeedPackImportItem Item(SheetRow row, string key, string entity, string outcome, string message,
        long? requestId = null, long? jdId = null, long? candidateId = null, long? applicationId = null, decimal? atsScore = null) => new()
    {
        Sheet = row.Sheet, RowNumber = row.RowNumber, SourceKey = key, Entity = entity, Outcome = outcome, Message = message,
        RequisitionId = requestId, JobDescriptionId = jdId, CandidateId = candidateId, ApplicationId = applicationId, AtsScore = atsScore
    };

    private static bool IsActionable(SheetRow row)
    {
        var key = row.Value("Source Key", row.Value("Candidate Key"));
        return key.Length > 0 && !key.StartsWith("EXAMPLE", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DuplicateKeyError(IEnumerable<SheetRow> rows, string column, string sheet)
    {
        var duplicate = rows.GroupBy(row => row.Value(column), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"{sheet} contains duplicate {column} '{duplicate.Key}'.";
    }

    private static List<string> Split(string value) => value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static string NormalizeEmail(string value) => value.Trim().ToLowerInvariant();
    private static string NormalizePhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    private static async Task<Workbook> ReadWorkbookAsync(IFormFile file, CancellationToken cancellationToken)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await using var source = file.OpenReadStream();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;
        using var reader = ExcelReaderFactory.CreateReader(memory);
        var sheets = new Dictionary<string, List<SheetRow>>(StringComparer.OrdinalIgnoreCase);
        do
        {
            var raw = new List<List<string>>();
            while (reader.Read())
            {
                var cells = new List<string>(reader.FieldCount);
                for (var index = 0; index < reader.FieldCount; index++) cells.Add(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)?.Trim() ?? "");
                raw.Add(cells);
            }
            var headerIndex = raw.FindIndex(row => row.Any(value => value.Length > 0));
            if (headerIndex < 0) { sheets[reader.Name] = []; continue; }
            var headers = raw[headerIndex].Select((value, index) => (Name: value.Trim(), Index: index)).Where(item => item.Name.Length > 0).ToList();
            sheets[reader.Name] = raw.Skip(headerIndex + 1).Select((cells, index) => new SheetRow(reader.Name, headerIndex + index + 2,
                headers.ToDictionary(header => header.Name, header => header.Index < cells.Count ? cells[header.Index] : "", StringComparer.OrdinalIgnoreCase))).ToList();
        } while (reader.NextResult());
        return new Workbook(sheets);
    }

    private sealed record Workbook(Dictionary<string, List<SheetRow>> Sheets)
    {
        public IReadOnlyList<SheetRow> Rows(string sheet) => Sheets.TryGetValue(sheet, out var rows) ? rows : [];
        public string? Require(string sheet, params string[] headers)
        {
            if (!Sheets.TryGetValue(sheet, out var rows)) return $"Required sheet '{sheet}' is missing. Use the latest downloaded template.";
            IEnumerable<string> available = rows.Count > 0 ? rows[0].Values.Keys : Array.Empty<string>();
            var missing = headers.Where(header => !available.Contains(header, StringComparer.OrdinalIgnoreCase)).ToList();
            return missing.Count == 0 ? null : $"Sheet '{sheet}' is missing column(s): {string.Join(", ", missing)}.";
        }
    }

    private sealed record SheetRow(string Sheet, int RowNumber, Dictionary<string, string> Values)
    {
        public string Value(string name, string fallback = "") => Values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
        public string Required(string name) => Value(name) is { Length: > 0 } value ? value : throw new ImportValidationException($"{Sheet} row {RowNumber}: {name} is required.");
        public int Int(string name, int fallback = 0) => int.TryParse(Value(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        public decimal Decimal(string name, decimal fallback = 0) => decimal.TryParse(Value(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        public decimal? NullableDecimal(string name) => decimal.TryParse(Value(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
        public bool Yes(string name) => new[] { "YES", "Y", "TRUE", "1", "MANDATORY", "REQUIRED" }.Contains(Value(name).ToUpperInvariant());
        public DateTime? Date(string name) => DateTime.TryParse(Value(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value) ? value.Date : null;
        public string Action(string name) => Value(name, "Draft").Trim().ToUpperInvariant();
    }

    private sealed class ImportValidationException(string message) : Exception(message);
}
