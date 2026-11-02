using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Services;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ResumeTimelineLayoutTests
{
    private static ResumeParsingService Parser()=>new(null!,null!,NullLogger<ResumeParsingService>.Instance);
    private static async Task<ResumeParseResult> Parse(string text)
    {
        var bytes=Encoding.UTF8.GetBytes(text);
        return await Parser().ParseAsync(new MemoryStream(bytes),"Asha_Kumar.txt",bytes.Length,CancellationToken.None);
    }
    private static string Columns()=>string.Join('\n',new (string Left,string Right)[]{
        ("Pune, India","Asha Kumar"),
        ("asha@example.test","PROFESSIONAL EXPERIENCE"),
        ("EXECUTIVE PROFILE","April 2021 - Current"),
        ("Executive with 34+ years of global","Head, Innovation Centers and CoE | AI Chief Architect"),
        ("R&D and platform leadership.","APAC Acme Research Labs | Pune, India"),
        ("SKILLS","• Led engineering teams across multiple countries."),
        ("• Cloud architecture","April 2017 - April 2021"),
        ("• Database management","Chief Software Architect | Earlier Systems Ltd"),
        ("• Strong artificial intelligence","Pune, India"),
        ("  background","• Directed engineering and release management."),
        ("• Artificial intelligence design","EDUCATION AND EXECUTIVE DEVELOPMENT"),
        ("• LLM & SLM Design & development","MBA | Global Management | Example University"),
        ("WEBSITES",""),
        ("example.test","")
    }.Select(row=>row.Left.PadRight(42)+row.Right));

    [Fact]
    public async Task IndependentSidebarCannotSupplyEmployerOrDegreeAndCurrentTimelineWins()
    {
        var text=ResumeParsingService.RestoreIndependentPdfColumns(Columns());
        var result=await Parse(text);
        Assert.Equal("APAC Acme Research Labs",result.Facts.CurrentCompany);
        Assert.Contains("Head, Innovation Centers and CoE",result.Facts.CurrentTitle);
        Assert.Contains("AI Chief Architect",result.Facts.CurrentTitle);
        Assert.DoesNotContain("Chief Software Architect",result.Facts.CurrentTitle);
        Assert.Equal("Pune, India",result.Facts.CurrentLocation);
        Assert.Contains("Cloud architecture",result.Facts.Skills);
        Assert.Contains("Artificial intelligence design",result.Facts.Skills);
        Assert.Contains("Strong artificial intelligence background",result.Facts.Skills);
        Assert.Contains("MBA",result.Facts.HighestQualification);
        Assert.DoesNotContain("LLM",result.Facts.HighestQualification);
        Assert.Equal(408,result.Facts.TotalExperienceMonths);
        Assert.True(result.Facts.Experience[0].IsCurrent);
        Assert.Equal(new DateOnly(2021,4,1),result.Facts.Experience[0].StartDate);
    }

    [Theory]
    [InlineData("Current")][InlineData("Present")][InlineData("Currently")]
    [InlineData("Still working")][InlineData("Still in company")][InlineData("Onwards")][InlineData("Till date")]
    public async Task ExplicitOngoingRoleWinsOverNewerHistoricalStart(string ongoing)
    {
        var text=$"Asha Kumar\nasha@example.test\nPROFESSIONAL EXPERIENCE\nJanuary 2024 - December 2025\nSenior Engineer | Historical Systems Ltd\nPune, India\nJanuary 2020 - {ongoing}\nChief Architect | Active Research Labs\nPune, India\nSKILLS\nSQL, Cloud architecture\n";
        var result=await Parse(text);
        Assert.Equal("Active Research Labs",result.Facts.CurrentCompany);
        Assert.Equal("Chief Architect",result.Facts.CurrentTitle);
    }

    [Fact]
    public async Task MostRecentlyEndedEmploymentWinsWhenNoOngoingRoleExists()
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nEXPERIENCE\nJanuary 2023 - December 2024\nSenior Engineer | Earlier End Ltd\nJanuary 2010 - December 2025\nDirector | Latest End Ltd\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Latest End Ltd",result.Facts.CurrentCompany);
    }

    [Fact]
    public async Task EmployerBeforeDatesAndRoleAfterDatesStayOnTheSameRow()
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nEXPERIENCE\nExample Group (Sep 2023 - Present) - Head of Digital Transformation\nBuilt the technology team from the ground up.\nEarlier Systems (Nov 2021 - Sep 2023) - CTO\nGrew the team from 10 to 30.\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Example Group",result.Facts.CurrentCompany);
        Assert.Equal("Head of Digital Transformation",result.Facts.CurrentTitle);
    }

    [Fact]
    public async Task EmployerAndTitleBeforeDateRowAreSplitInsteadOfBorrowingResponsibilities()
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nEXPERIENCE\nExample Systems Ltd. - Associate Vice President, Product Innovation\nJuly 2017 - Present | Pune\nLed engineering teams across multiple countries.\nEarlier Labs - Chief Technology Officer\nApril 2018 - December 2021 | Pune\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Example Systems Ltd.",result.Facts.CurrentCompany);
        Assert.Equal("Associate Vice President, Product Innovation",result.Facts.CurrentTitle);
    }

    [Fact]
    public async Task ReportingManagersAfterDatesCannotReplaceCandidateRole()
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nEXPERIENCE\nDirector of Technology | Global Head, Platform Engineering\nExample Systems Ltd. | India | Sep 2021 - Present | Reports to Global Head Engineering | 700 engineers\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Director of Technology",result.Facts.CurrentTitle);
        Assert.Equal("Example Systems Ltd.",result.Facts.CurrentCompany);
        Assert.DoesNotContain("Reports",result.Facts.CurrentTitle);
    }

    [Fact]
    public async Task EmployerDateThenBusinessDescriptionAndRoleKeepsReportingMetadataOut()
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nEXECUTIVE CAREER SYNOPSIS & HIGHLIGHTS\nExample Systems Ltd. ---- Sep 2023 - Present\nBill payment platform serving enterprise customers\nChief Technology Officer I Reporting to: CEO I Team Size: 150\nAppointed as Chief Technology Officer to architect the next phase of platform modernisation.\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Chief Technology Officer",result.Facts.CurrentTitle);
        Assert.Equal("Example Systems Ltd.",result.Facts.CurrentCompany);
    }

    [Fact]
    public async Task ResponsibilitiesCannotBeAnEmployerWhenEmployerIsUnknown()
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nEXPERIENCE\nApril 2021 - Present\nChief Technology Officer\n● Spearheaded technology and product strategy for the platform.\nEstablished best practices for API design and execution.\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Chief Technology Officer",result.Facts.CurrentTitle);
        Assert.Empty(result.Facts.CurrentCompany);
    }

    [Fact]
    public async Task WrappedEmailIsJoinedOnlyInsideAnIndependentPdfColumn()
    {
        var text=Columns().Replace("asha@example.test".PadRight(42),"asha@exampl".PadRight(42))
            .Replace("EXECUTIVE PROFILE","e.test".PadRight(42)+"\nEXECUTIVE PROFILE");
        var result=await Parse(ResumeParsingService.RestoreIndependentPdfColumns(text));
        Assert.Equal("asha@example.test",result.Facts.Email);
    }

    [Fact]
    public async Task EmailCannotSupplyMissingNameTokensForFilenameHint()
    {
        var result=await Parse("Asha\nasha.kumar@example.test\nSUMMARY\nTechnology professional building reliable platforms for enterprise customers.\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Asha",result.Facts.FullName);
    }

    [Fact]
    public async Task SkillsetHeadingAndRoundBulletsKeepActualToolsSeparateFromResponsibilities()
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nEXPERIENCE\nChief Technology Officer | Jan 2023 onwards\nExample Systems Ltd. | Pune\n● Led a cross-functional engineering team.\nEDUCATION\nMBA | Example University\nSkillset\n● Openstack, Ceph, K8s\n● Core Java, Event Bus\n  Kafka\n");
        Assert.Contains("Openstack",result.Facts.Skills);
        Assert.Contains("Ceph",result.Facts.Skills);
        Assert.Contains("K8s",result.Facts.Skills);
        Assert.Contains("Event Bus Kafka",result.Facts.Skills);
        Assert.DoesNotContain(result.Facts.Skills,skill=>skill.Contains("Led"));
        Assert.True(result.Facts.Experience[0].IsCurrent);
    }

    [Fact]
    public void AlignedTablesWithoutIndependentSidebarHeadingsKeepTheirRowAssociations()
    {
        const string table="Asha Kumar\nPROFESSIONAL EXPERIENCE\nRole                          Company                   Dates\nSenior Engineer               Acme Ltd                  2021 - Present\nSKILLS\nSQL, Azure\n";
        Assert.Equal(table,ResumeParsingService.RestoreIndependentPdfColumns(table));
    }

    [Theory]
    [InlineData("SKILLS\nLLM & SLM Design & development\nCloud architecture",false)]
    [InlineData("SKILLS\nDefined an LLM and SLM model strategy trained on jurisdiction-specific legal corpora.\nCloud architecture",false)]
    [InlineData("EDUCATION\nLL.M. | Law | Example University",true)]
    public async Task LanguageModelSkillsAreNotLawDegrees(string evidence,bool lawDegree)
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nSUMMARY\nTechnology professional delivering reliable applications for enterprise customers.\n"+evidence);
        if(lawDegree)Assert.Contains("LL.M",result.Facts.HighestQualification);
        else Assert.Empty(result.Facts.HighestQualification);
    }

    [Fact]
    public async Task AdminAndPublicFileEntryPointsShareTheSameCoreFacts()
    {
        var text=ResumeParsingService.RestoreIndependentPdfColumns(Columns());
        var bytes=Encoding.UTF8.GetBytes(text);
        var admin=await Parser().ParseAsync(new FormFile(new MemoryStream(bytes),0,bytes.Length,"file","Asha_Kumar.txt"),CancellationToken.None);
        var published=await Parser().ParseAsync(new FormFile(new MemoryStream(bytes),0,bytes.Length,"file","Asha_Kumar.txt"),20,new[]{"Chief Technology Officer"},CancellationToken.None);
        Assert.Equal(admin.Facts.CurrentCompany,published.Facts.CurrentCompany);
        Assert.Equal(admin.Facts.CurrentTitle,published.Facts.CurrentTitle);
        Assert.Equal(admin.Facts.HighestQualification,published.Facts.HighestQualification);
        Assert.Equal(admin.Facts.Skills,published.Facts.Skills);
        var values=RecruitmentFormRepository.BuildResumePrefillValues(published,new[]{
            (1L,"CURRENT_COMPANY"),(2L,"CURRENT_DESIGNATION"),(3L,"HIGHEST_QUALIFICATION"),
            (4L,"SKILLS"),(5L,"TOTAL_EXPERIENCE_MONTHS"),(6L,"EMAIL"),(7L,"FIRST_NAME")
        }).ToDictionary(value=>value.FieldId);
        Assert.Equal(admin.Facts.CurrentCompany,values[1].TextValue);
        Assert.Equal(admin.Facts.CurrentTitle,values[2].TextValue);
        Assert.Equal(admin.Facts.HighestQualification,values[3].TextValue);
        Assert.Contains("Artificial intelligence design",values[4].TextValue);
        Assert.Equal(admin.Facts.TotalExperienceMonths,values[5].IntegerValue);
        Assert.Equal(admin.Facts.Email,values[6].TextValue);
        Assert.Equal("Asha",values[7].TextValue);
    }

    [Fact]
    public async Task PublicPrefillCannotReintroduceDateAsTitleWhenSharedFactsAreUnknown()
    {
        var result=await Parse("Asha Kumar\nasha@example.test\nEXPERIENCE\nApril 2021 - Current\nEngineering platforms that support customers.\nEDUCATION\nJanuary 2020\nSKILLS\nSQL, Azure\n");
        Assert.Empty(result.Facts.CurrentTitle);
        var values=RecruitmentFormRepository.BuildResumePrefillValues(result,new[]{
            (1L,"CURRENT_COMPANY"),(2L,"CURRENT_DESIGNATION"),(3L,"HIGHEST_QUALIFICATION")
        });
        Assert.Empty(values);
    }
}
