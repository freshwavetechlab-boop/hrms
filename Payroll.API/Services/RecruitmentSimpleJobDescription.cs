using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Payroll.API.Services;

public sealed record SimpleJobDescriptionContent(
    string Title,
    string Summary,
    string RolePurpose,
    List<string> Responsibilities,
    List<string> Skills);

/// <summary>Title-based role guidance, without inferred hiring eligibility or employer policy.</summary>
public static class RecruitmentSimpleJobDescription
{
    public const int MaximumTitleLength = 180;
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string NormalizeTitleKey(string positionTitle) => CleanTitle(positionTitle).ToUpperInvariant();

    public static SimpleJobDescriptionContent Generate(string positionTitle)
    {
        var title = CleanTitle(positionTitle);
        var family = Families.FirstOrDefault(row => Regex.IsMatch(title, row.Pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) ?? General;
        return new(title,
            $"The {title} role supports {family.Focus} through accurate work, clear communication and dependable delivery.",
            family.Purpose,
            family.Responsibilities.ToList(),
            family.Skills.ToList());
    }

    private static string CleanTitle(string positionTitle)
    {
        if (positionTitle is null) throw InvalidTitle();
        string normalized;
        try { normalized = positionTitle.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { throw InvalidTitle(); }
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                throw InvalidTitle();
        }
        var title = Spaces.Replace(normalized.Trim(), " ");
        var length = title.EnumerateRunes().Count();
        if (length < 1 || length > MaximumTitleLength) throw InvalidTitle();
        return title;
    }

    private static ArgumentException InvalidTitle() => new(
        $"Enter a position title of 1 to {MaximumTitleLength} characters without control characters.", "positionTitle");

    private sealed record RoleFamily(string Pattern, string Focus, string Purpose, string[] Responsibilities, string[] Skills);

    // Match whole role words so, for example, "Account Manager" does not become an accountant.
    private const string Start = @"(?<![\p{L}\p{N}])(?:";
    private const string End = @")(?![\p{L}\p{N}])";
    private static readonly RoleFamily[] Families =
    [
        new(Start + @"hr|human resources|recruit(?:er|ment|ing)|talent acquisition" + End,
            "people and recruitment operations", "Support organised, responsive people processes and a consistent experience for employees and candidates.",
            ["Coordinate assigned recruitment or employee-service activities and follow up on pending actions.",
             "Maintain accurate candidate, employee and process records with appropriate confidentiality.",
             "Arrange meetings, interviews or employee communications with the relevant participants.",
             "Track requests and progress, prepare routine reports and highlight unresolved issues.",
             "Work with colleagues to keep people processes clear, timely and consistent."],
            ["People coordination", "Written and verbal communication", "Record management", "Confidential handling of information", "Planning and follow-up"]),
        new(Start + @"accountant|accounts|accounting|finance|financial|audit(?:or|ing)?|payroll|tax|treasury|bookkeep(?:er|ing)|cashier" + End,
            "financial records and transaction processes", "Support accurate financial information, orderly supporting records and timely resolution of transaction queries.",
            ["Prepare and maintain assigned transaction, invoice or accounting records with supporting documents.",
             "Check entries and reconcile balances against available source records.",
             "Track pending receipts, payments or queries and coordinate their resolution.",
             "Prepare routine summaries and provide records for internal review.",
             "Handle financial information carefully and report discrepancies to the responsible person."],
            ["Numerical accuracy", "Reconciliation", "Spreadsheet use", "Document checking", "Financial record management"]),
        new(Start + @"it support|technical support|desktop support|help\s?desk|system(?:s)? administrator|database admin(?:istrator)?|dba|network(?:ing)?(?: engineer| administrator)?" + End,
            "reliable technology support", "Help users resolve technology issues and keep assigned systems and support records organised.",
            ["Receive, record and prioritise assigned user incidents and service requests.",
             "Investigate hardware, software or connectivity issues and document troubleshooting steps.",
             "Carry out authorised setup, configuration and routine maintenance tasks.",
             "Escalate unresolved problems with clear details and follow up with users.",
             "Maintain support notes and handle access and user information according to assigned controls."],
            ["Troubleshooting", "User communication", "Incident documentation", "System configuration", "Issue prioritisation"]),
        new(Start + @"software|developer|development engineer|programmer|devops|web engineer|data engineer|application engineer|qa|tester|testing engineer" + End,
            "software delivery and maintenance", "Help deliver understandable, tested and maintainable software that addresses agreed requirements.",
            ["Clarify assigned requirements and break work into practical implementation tasks.",
             "Develop, configure or test software changes appropriate to the assigned role.",
             "Investigate defects, document findings and verify corrections.",
             "Maintain clear technical notes and participate in collaborative reviews.",
             "Coordinate releases and follow up on issues affecting delivered functionality."],
            ["Problem solving", "Software development practices", "Testing and debugging", "Technical documentation", "Team collaboration"]),
        new(Start + @"nurse|nursing|patient care|clinical assistant|healthcare assistant" + End,
            "organised patient-care activities", "Support safe, attentive patient-care activities within the assigned role, authorised scope and applicable care protocols.",
            ["Carry out assigned care-support activities within the role's authorised scope.",
             "Record relevant observations accurately and communicate concerns to the responsible care professional.",
             "Help maintain organised care records, supplies and handovers.",
             "Communicate respectfully with patients, families and care-team members.",
             "Follow applicable hygiene, confidentiality and escalation procedures."],
            ["Care coordination", "Accurate observation and recording", "Patient communication", "Team handover", "Attention to hygiene and confidentiality"]),
        new(Start + @"laboratory|lab|laboratorian|pathology" + End,
            "laboratory activities and records", "Support consistent laboratory work through careful preparation, traceable records and attention to assigned procedures.",
            ["Prepare assigned materials, samples or equipment using the relevant laboratory procedures.",
             "Carry out assigned laboratory tasks within authorised responsibilities.",
             "Label and record samples, observations and results accurately for traceability.",
             "Check equipment and supplies and report irregularities to the responsible person.",
             "Maintain an orderly work area and follow applicable handling and safety procedures."],
            ["Laboratory record keeping", "Sample traceability", "Procedural accuracy", "Equipment care", "Attention to detail"]),
        new(Start + @"research|researcher|scientist|scientific|study coordinator" + End,
            "research activities and evidence management", "Support well-organised research with careful information gathering, transparent records and clear reporting.",
            ["Support the planning and coordination of assigned research activities.",
             "Collect and organise relevant information or data using the agreed study approach.",
             "Check data quality and maintain traceable notes on methods and observations.",
             "Summarise findings and prepare reports or supporting materials for review.",
             "Coordinate with the research team and follow applicable study and information-handling procedures."],
            ["Information gathering", "Data quality checking", "Research documentation", "Analytical thinking", "Report writing"]),
        new(Start + @"data entry|computer operator|documentation|records clerk" + End,
            "accurate information and document processing", "Keep assigned data and records complete, consistent and easy to retrieve.",
            ["Enter or update information from available source documents accurately.",
             "Check records for missing values, duplicates and obvious inconsistencies.",
             "Organise files and maintain clear references to source information.",
             "Prepare routine extracts or summaries and track pending corrections.",
             "Handle records confidentially and raise unresolved data issues for review."],
            ["Accurate data entry", "Spreadsheet use", "Document organisation", "Data checking", "Attention to detail"]),
        new(Start + @"admin(?:istration|istrative|istrator)?|office assistant|office executive|receptionist|front desk|office coordinator" + End,
            "day-to-day office operations", "Help the office run smoothly through organised coordination, reliable records and timely communication.",
            ["Coordinate assigned office activities, schedules and routine requests.",
             "Prepare and maintain correspondence, files and administrative records.",
             "Support meetings, visitors or shared office services as assigned.",
             "Track supplies, requests and pending actions and follow up with the relevant people.",
             "Provide clear updates and report issues affecting office operations."],
            ["Office coordination", "Document management", "Communication", "Scheduling", "Follow-up and prioritisation"]),
        new(Start + @"sales|marketing|account manager|business development|customer relationship" + End,
            "customer relationships and business activities", "Support responsive customer engagement, clear communication and organised follow-up on agreed business activities.",
            ["Understand assigned customer or market needs and maintain accurate interaction records.",
             "Coordinate enquiries, proposals or outreach activities with the relevant colleagues.",
             "Follow up on agreed actions and communicate progress clearly.",
             "Prepare routine activity summaries and highlight opportunities or unresolved concerns.",
             "Maintain professional relationships and use approved information in external communications."],
            ["Customer communication", "Relationship management", "Activity tracking", "Presentation", "Planning and follow-up"])
    ];

    private static readonly RoleFamily General = new("", "the assigned operational activities",
        "Deliver the assigned role's work reliably, maintain clear records and coordinate effectively with the responsible team.",
        ["Carry out assigned tasks in line with agreed priorities and procedures.",
         "Maintain accurate records and provide clear updates on work completed and pending.",
         "Coordinate with colleagues and relevant contacts to resolve routine requests.",
         "Check work for accuracy and highlight issues needing a decision or additional support.",
         "Handle shared information responsibly and contribute to orderly day-to-day operations."],
        ["Task planning", "Clear communication", "Record keeping", "Attention to detail", "Team coordination"]);
}
