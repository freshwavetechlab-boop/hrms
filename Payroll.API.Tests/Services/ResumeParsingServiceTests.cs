using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumeParsingServiceTests
{
    private static readonly CancellationToken NoCancellation = CancellationToken.None;

    [Fact]
    public async Task Docx_ArjunSharma_ExtractsAtsIdentityExperienceAndSectionsWithoutAi()
    {
        var sourceText = await LoadFixtureAsync("ArjunSharmaResume.txt");
        var bytes = CreateDocx(sourceText);

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes),
            "Resume_1_ARJUN_SHARMA.docx",
            bytes.Length,
            NoCancellation);

        Assert.Equal("Parsed", result.Status);
        Assert.Equal("ARJUN SHARMA", result.Facts.FullName);
        Assert.Equal("arjun.sharma.dev@example.com", result.Facts.Email);
        Assert.Equal("+91-98765 43210", result.Facts.Phone);
        Assert.Equal(84, result.Facts.TotalExperienceMonths);
        Assert.Equal("Accenture", result.Facts.CurrentCompany);
        Assert.Equal("Senior DevOps Engineer", result.Facts.CurrentTitle);
        Assert.Equal("Bengaluru, India", result.Facts.CurrentLocation);
        Assert.Contains("B.E.", result.Facts.HighestQualification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Jenkins", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Kubernetes", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Terraform", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Microsoft Certified: Azure Fundamentals", result.Facts.Certifications, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, result.Facts.Experience.Count);
        Assert.Contains(result.Facts.Experience, row => row.Company == "Accenture"
            && row.JobTitle == "Senior DevOps Engineer" && row.IsCurrent);
        Assert.Contains(result.Facts.Experience, row => row.Company == "Tech Mahindra"
            && row.JobTitle == "DevOps Engineer" && !row.IsCurrent);
        Assert.Contains(result.Facts.Education, row => row.Qualification.Contains("B.E.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Sections, section => section.SectionCode == "SUMMARY"
            && section.Content.Contains("DevOps Engineer with 7+ years", StringComparison.Ordinal));
        Assert.Contains(result.Sections, section => section.SectionCode == "SKILLS"
            && section.Content.Contains("Kubernetes", StringComparison.Ordinal));
        Assert.Contains(result.Sections, section => section.SectionCode == "CERTIFICATIONS"
            && section.Content.Contains("Azure Fundamentals", StringComparison.Ordinal));
        Assert.Contains(result.Sections, section => section.SectionCode == "EDUCATION"
            && section.Content.Contains("Computer Science Engineering", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Docx_TableStyleEmployment_ExtractsCurrentRoleAndSkillsWithoutAi()
    {
        const string text = """
            AMAN GUPTA
            aman.gupta.devops@example.com • +91 97654 21890
            CAREER OVERVIEW
            DevOps Engineer with 6+ years of experience in cloud delivery.
            EMPLOYMENT HISTORY
            Role
            Organization
            Location
            Dates
            DevOps Engineer
            Deloitte
            Hyderabad, India
            Feb 2023 - Present
            CORE SKILLS
            Area
            Technologies
            CI/CD
            Azure DevOps; Jenkins; GitHub Actions
            Containers
            Docker; Kubernetes; Helm
            EDUCATION
            B.Tech | Information Technology | Pune University | 2014-2018
            """;
        var bytes = CreateDocx(text);

        var result = await CreateParser().ParseAsync(new MemoryStream(bytes), "Aman_Gupta.docx", bytes.Length, NoCancellation);

        Assert.Equal("DevOps Engineer", result.Facts.CurrentTitle);
        Assert.Equal("Deloitte", result.Facts.CurrentCompany);
        Assert.Equal("Hyderabad, India", result.Facts.CurrentLocation);
        Assert.Contains("Kubernetes", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Area", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Technologies", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_TimelineStyleEmployment_ExtractsCurrentRoleAndCompanyWithoutAi()
    {
        const string text = """
            VIVEK SINGH
            vivek.singh.cloud@example.com | +919899045231
            ABOUT ME
            Automation Engineer with 8+ years of experience.
            CAREER TIMELINE
            2022 – Present
            Senior DevOps Engineer — Cognizant
            Chennai, India
            2019 – 2022
            DevOps Engineer — Mindtree
            Bengaluru, India
            SKILLS MATRIX
            Jenkins, GitLab CI, Docker, Kubernetes, Terraform
            CERTIFICATIONS & EDUCATION
            CERTIFICATION: AWS Cloud Practitioner
            EDUCATION: B.E. Computer Science, University of Pune, 2013 to 2017
            """;
        var bytes = CreateDocx(text);

        var result = await CreateParser().ParseAsync(new MemoryStream(bytes), "Vivek_Singh.docx", bytes.Length, NoCancellation);

        Assert.Equal("Senior DevOps Engineer", result.Facts.CurrentTitle);
        Assert.Equal("Cognizant", result.Facts.CurrentCompany);
        Assert.Equal("Chennai, India", result.Facts.CurrentLocation);
        Assert.Contains("AWS Cloud Practitioner", result.Facts.Certifications, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Facts.Certifications, value => value.StartsWith("EDUCATION", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Odt_StructuredResume_ExtractsAtsFactsWithoutAi()
    {
        const string text = """
            MEERA IYER
            Email: meera.iyer@example.com | Mobile: +91 99887 66554
            PROFESSIONAL SUMMARY
            Cloud Platform Engineer with 9+ years of experience building reliable infrastructure.
            WORK EXPERIENCE
            Lead Platform Engineer | Infosys | Pune, India | Mar 2021 - Present
            DevOps Engineer | Wipro | Bengaluru, India | Jul 2017 - Feb 2021
            TECHNICAL SKILLS
            AWS, Azure, Kubernetes, Terraform, Jenkins, Linux
            CERTIFICATIONS
            AWS Certified Solutions Architect
            EDUCATION
            M.Tech | Computer Science | University of Pune | 2015 - 2017
            """;
        var bytes = CreateOdt(text);

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Meera_Iyer.odt", bytes.Length, NoCancellation);

        Assert.Equal("Parsed", result.Status);
        Assert.Equal("MEERA IYER", result.Facts.FullName);
        Assert.Equal("meera.iyer@example.com", result.Facts.Email);
        Assert.Equal("+91 99887 66554", result.Facts.Phone);
        Assert.Equal(108, result.Facts.TotalExperienceMonths);
        Assert.Equal("Lead Platform Engineer", result.Facts.CurrentTitle);
        Assert.Equal("Infosys", result.Facts.CurrentCompany);
        Assert.Equal("Pune, India", result.Facts.CurrentLocation);
        Assert.Contains("Kubernetes", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AWS Certified Solutions Architect", result.Facts.Certifications, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("M.Tech", result.Facts.HighestQualification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Sections, section => section.SectionCode == "SUMMARY");
        Assert.Contains(result.Sections, section => section.SectionCode == "EXPERIENCE");
        Assert.Contains(result.Sections, section => section.SectionCode == "SKILLS");
    }

    [Fact]
    public async Task Txt_LabelledPhoneWinsOverEmployeeAndAccountNumbers()
    {
        const string text = """
            Name: Neha Kapoor
            Employee ID: 9876543210
            Payroll account: 8765432109
            Email: neha.kapoor@example.com
            Mobile: +91 99887 66554
            PROFESSIONAL SUMMARY
            Software Engineer with 5 years of experience in enterprise applications.
            TECHNICAL SKILLS
            C#, ASP.NET Core, SQL Server
            """;
        var bytes = Encoding.UTF8.GetBytes(text);

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Neha_Kapoor.txt", bytes.Length, NoCancellation);

        Assert.Equal("Parsed", result.Status);
        Assert.Equal("+91 99887 66554", result.Facts.Phone);
    }

    [Fact]
    public async Task Txt_UnicodeDashAndAbbreviatedDates_CreateStructuredExperience()
    {
        var text = """
            Name: Kabir Rao
            Email: kabir.rao@example.com
            Phone: +91 98765 01234
            EXECUTIVE PROFILE
            Platform specialist building cloud automation and resilient delivery systems.
            PROFESSIONAL BACKGROUND
            Senior Platform Engineer | Acme Cloud | Bengaluru, India | Sep '21 – Present
            DevOps Engineer | Contoso Systems | Pune, India | 06/18 – 08/21
            TECHNOLOGIES & FRAMEWORKS
            Kubernetes, Terraform, Jenkins, Azure
            """;
        text = text.Replace("\u00e2\u20ac\u201c", "\u2013", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text);

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Kabir_Rao.txt", bytes.Length, NoCancellation);

        Assert.Equal("Parsed", result.Status);
        Assert.Equal("Senior Platform Engineer", result.Facts.CurrentTitle);
        Assert.Equal("Acme Cloud", result.Facts.CurrentCompany);
        Assert.Contains(result.Facts.Experience, row => row.IsCurrent
            && row.StartDate == new DateOnly(2021, 9, 1));
        Assert.Contains(result.Facts.Experience, row => !row.IsCurrent
            && row.StartDate == new DateOnly(2018, 6, 1)
            && row.EndDate == new DateOnly(2021, 8, 31));
    }

    [Fact]
    public async Task Txt_HeadingAliasesAndInlineCertifications_AreStructured()
    {
        const string text = """
            Name: Sana Khan
            Email: sana.khan@example.com
            Mobile: +91 91234 56780
            EXECUTIVE PROFILE
            Engineering leader with 12 years of experience delivering cloud platforms.
            PROFESSIONAL BACKGROUND
            Engineering Manager | Example Labs | Gurugram, India | January 2020 - Present
            TECHNOLOGIES & FRAMEWORKS
            Azure; Kubernetes; Terraform; .NET
            PROFESSIONAL CREDENTIALS
            Azure Solutions Architect; CKA; HashiCorp Terraform Associate
            EDUCATION
            B.Tech | Computer Science | Delhi University | 2012
            """;
        var bytes = Encoding.UTF8.GetBytes(text);

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Sana_Khan.txt", bytes.Length, NoCancellation);

        Assert.Contains(result.Sections, section => section.SectionCode == "SUMMARY"
            && section.Heading.Equals("EXECUTIVE PROFILE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Sections, section => section.SectionCode == "EXPERIENCE"
            && section.Heading.Equals("PROFESSIONAL BACKGROUND", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Sections, section => section.SectionCode == "SKILLS"
            && section.Heading.Equals("TECHNOLOGIES & FRAMEWORKS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Sections, section => section.SectionCode == "CERTIFICATIONS"
            && section.Heading.Equals("PROFESSIONAL CREDENTIALS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Kubernetes", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "Azure Solutions Architect", "CKA", "HashiCorp Terraform Associate" },
            result.Facts.Certifications);
    }

    [Fact]
    public async Task Txt_TabularColumns_KeepRoleCompanyLocationAndDatesDistinct()
    {
        var text = """
            Name: Rahul Sen
            Email: rahul.sen@example.com
            Phone: +91 90000 12345
            PROFESSIONAL SUMMARY
            DevOps professional focused on automation and platform reliability.
            WORK EXPERIENCE
            Role\tCompany\tLocation\tPeriod
            Lead DevOps Engineer\tTech Nova Pvt Ltd\tNoida, India\tOct 2022 - Present
            DevOps Engineer\tCloud Works\tPune, India\tJun 2019 - Sep 2022
            TECHNICAL SKILLS
            Jenkins\tDocker\tKubernetes\tTerraform
            """.Replace("\\t", "\t", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text);

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Rahul_Sen.txt", bytes.Length, NoCancellation);

        Assert.Equal("Lead DevOps Engineer", result.Facts.CurrentTitle);
        Assert.Equal("Tech Nova Pvt Ltd", result.Facts.CurrentCompany);
        Assert.Equal("Noida, India", result.Facts.CurrentLocation);
        Assert.Contains("Docker", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Kubernetes", result.Facts.Skills, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, result.Facts.Experience.Count);
    }

    [Fact]
    public async Task Txt_Utf16_ExtractsLabelledIdentityAndFormattedPhoneWithoutAi()
    {
        const string text = """
            Name: Priya Nair
            Email: priya.nair+jobs@example.co.in
            Mobile: +91 (98765) 41020
            PROFESSIONAL SUMMARY
            Cloud engineer with 10+ years of professional experience delivering resilient platforms.
            TECHNICAL SKILLS
            AWS, Terraform, Kubernetes, Linux, Python
            EDUCATION
            B.Tech in Information Technology
            """;
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Priya_Nair_resume.txt", bytes.Length, NoCancellation);

        Assert.Equal("Parsed", result.Status);
        Assert.Equal("Priya Nair", result.Facts.FullName);
        Assert.Equal("priya.nair+jobs@example.co.in", result.Facts.Email);
        Assert.Equal("+91 (98765) 41020", result.Facts.Phone);
        Assert.Equal(120, result.Facts.TotalExperienceMonths);
        Assert.Contains(result.Sections, section => section.SectionCode == "SKILLS");
    }

    [Fact]
    public async Task Rtf_ParagraphControlsRemainSectionBoundariesWithoutAi()
    {
        const string text = """
            VIVEK SINGH
            vivek.singh.cloud@example.com | +91 98990 45231
            PROFILE SUMMARY
            Site reliability engineer with 8 years of experience in cloud operations and automation.
            CORE COMPETENCIES
            Azure, Kubernetes, Docker, Helm, Prometheus
            CERTIFICATIONS & LICENSES
            Microsoft Certified Azure Administrator
            """;
        var bytes = Encoding.ASCII.GetBytes(CreateRtf(text));

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Vivek_Singh.rtf", bytes.Length, NoCancellation);

        Assert.Equal("Parsed", result.Status);
        Assert.Equal("VIVEK SINGH", result.Facts.FullName);
        Assert.Equal("vivek.singh.cloud@example.com", result.Facts.Email);
        Assert.Equal("+91 98990 45231", result.Facts.Phone);
        Assert.Equal(96, result.Facts.TotalExperienceMonths);
        Assert.Contains(result.Sections, section => section.SectionCode == "SUMMARY");
        Assert.Contains(result.Sections, section => section.SectionCode == "SKILLS");
        Assert.Contains(result.Sections, section => section.SectionCode == "CERTIFICATIONS");
    }

    [Fact]
    public async Task Pdf_TextLayer_ExtractsIdentityExperienceAndSectionsWithoutAi()
    {
        var lines = new[]
        {
            "KUNAL VERMA",
            "kunal.verma.cloud@example.com | +91-98123-45678",
            "CAREER SUMMARY",
            "Platform Engineer with 6+ years of experience in AWS automation and production operations.",
            "KEY SKILLS",
            "AWS, Docker, Kubernetes, Terraform, Jenkins, Linux",
            "QUALIFICATIONS",
            "B.E. Computer Science"
        };
        var bytes = CreateTextPdf(lines);

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Kunal_Verma.pdf", bytes.Length, NoCancellation);

        Assert.Equal("Parsed", result.Status);
        Assert.Equal("KUNAL VERMA", result.Facts.FullName);
        Assert.Equal("kunal.verma.cloud@example.com", result.Facts.Email);
        Assert.Equal("+91-98123-45678", result.Facts.Phone);
        Assert.Equal(72, result.Facts.TotalExperienceMonths);
        Assert.Contains(result.Sections, section => section.SectionCode == "SUMMARY");
        Assert.Contains(result.Sections, section => section.SectionCode == "SKILLS");
        Assert.Contains(result.Sections, section => section.SectionCode == "EDUCATION");
    }

    [Fact]
    public async Task Txt_TotalExperience_DoesNotCountEducationDateRange()
    {
        const string text = """
            Name: Nisha Verma
            Email: nisha.verma@example.com
            Phone: +91 98765 40123
            PROFESSIONAL SUMMARY
            Software engineer delivering reliable enterprise services.
            WORK EXPERIENCE
            Senior Software Engineer | Example Systems | Delhi, India | Jan 2020 - Present
            EDUCATION
            B.Tech | Computer Science | Delhi University | 2013 - 2017
            TECHNICAL SKILLS
            C#, .NET, SQL Server, Azure
            """;
        var bytes = Encoding.UTF8.GetBytes(text);

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Nisha_Verma.txt", bytes.Length, NoCancellation);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expectedMonths = (today.Year - 2020) * 12 + today.Month;
        Assert.Equal(expectedMonths, result.Facts.TotalExperienceMonths);
    }

    [Fact]
    public async Task UnsupportedOrBinaryContent_IsFlaggedForReviewInsteadOfInventingFacts()
    {
        var bytes = new byte[] { 0, 1, 2, 3, 4, 5, 255, 254 };

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "resume.bin", bytes.Length, NoCancellation);

        Assert.Equal("NeedsReview", result.Status);
        Assert.Empty(result.Text);
        Assert.Equal("", result.Facts.Email);
        Assert.Equal("", result.Facts.Phone);
        Assert.Equal("", result.Facts.FullName);
        Assert.Null(result.Facts.TotalExperienceMonths);
    }

    [Fact]
    public async Task MalformedBinaryContent_DoesNotInventIdentityFromFilename()
    {
        var bytes = new byte[] { 0, 255, 208, 13, 37, 80, 68, 0, 1, 2, 3, 254 };

        var result = await CreateParser().ParseAsync(
            new MemoryStream(bytes), "Rishav_Mishra_resume.bin", bytes.Length, NoCancellation);

        Assert.Equal("NeedsReview", result.Status);
        Assert.Empty(result.Text);
        Assert.Equal("", result.Facts.FullName);
        Assert.Equal("", result.Facts.Email);
        Assert.Equal("", result.Facts.Phone);
    }

    private static ResumeParsingService CreateParser() => new(
        aiScoring: null!,
        documentRag: null!,
        NullLogger<ResumeParsingService>.Instance);

    private static Task<string> LoadFixtureAsync(string name) => File.ReadAllTextAsync(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name),
        Encoding.UTF8);

    private static byte[] CreateDocx(string sourceText)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("word/document.xml");
            using var stream = entry.Open();
            var word = (XNamespace)"http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var body = new XElement(word + "body",
                sourceText.Split('\n').Select(line =>
                    new XElement(word + "p", new XElement(word + "r", new XElement(word + "t", line.TrimEnd('\r'))))));
            new XDocument(new XElement(word + "document",
                new XAttribute(XNamespace.Xmlns + "w", word), body)).Save(stream);
        }
        return memory.ToArray();
    }

    private static byte[] CreateOdt(string sourceText)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimeType = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimeType.Open(), new UTF8Encoding(false), leaveOpen: false))
                writer.Write("application/vnd.oasis.opendocument.text");

            var content = archive.CreateEntry("content.xml");
            using var stream = content.Open();
            XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
            XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
            var document = new XElement(office + "document-content",
                new XAttribute(XNamespace.Xmlns + "office", office),
                new XAttribute(XNamespace.Xmlns + "text", text),
                new XElement(office + "body",
                    new XElement(office + "text",
                        sourceText.Split('\n').Select(line =>
                            new XElement(text + "p", line.TrimEnd('\r'))))));
            new XDocument(document).Save(stream);
        }
        return memory.ToArray();
    }

    private static string CreateRtf(string sourceText)
    {
        static string Escape(string value) => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal);

        return "{\\rtf1\\ansi " + string.Join("\\par ", sourceText.Split('\n').Select(line => Escape(line.TrimEnd('\r')))) + "}";
    }

    private static byte[] CreateTextPdf(IReadOnlyList<string> lines)
    {
        static string EscapePdf(string value) => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

        var content = new StringBuilder("BT\n/F1 11 Tf\n72 750 Td\n");
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0) content.Append("0 -16 Td\n");
            content.Append('(').Append(EscapePdf(lines[index])).Append(") Tj\n");
        }
        content.Append("ET\n");

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content.ToString())} >>\nstream\n{content}endstream"
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
