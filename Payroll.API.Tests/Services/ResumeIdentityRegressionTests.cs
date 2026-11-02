using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumeIdentityRegressionTests
{
    private static async Task<ResumeParseResult> Parse(string header,string file="resume.txt")
    {
        var bytes=Encoding.UTF8.GetBytes(header+"\nCAREER SUMMARY\nProfessional with extensive delivery experience supporting complex public platforms and reliable services.\nSKILLS\nC#, SQL, Azure, Project Management\n");
        return await new ResumeParsingService(null!,null!,NullLogger<ResumeParsingService>.Instance)
            .ParseAsync(new MemoryStream(bytes),file,bytes.Length,CancellationToken.None);
    }

    [Theory]
    [InlineData("CONTACT ME AT | MEERA RAO\nmeera@example.com","30_Meera_Rao.txt","MEERA RAO")]
    [InlineData("CURICULUM VITAE\nMohan Sharma, Ph.D.\nmohan@example.com","25_Mohan_Sharma_Ph_D.txt","Mohan Sharma")]
    [InlineData("RAVI P KUMAR\nTechnology Leader | CTO\nravi@example.com","37_Ravi_P_Kumar.txt","RAVI P KUMAR")]
    [InlineData("Wg Cdr (Dr.) Arjun Kapoor (Retd.)\narjun@example.com","21_Wg_Cdr_Dr_Arjun_Kapoor_Retd.txt","Arjun Kapoor")]
    [InlineData("Bengaluru, India | Suman\nPrakash\n+91 9876543210 | Nair\nname@example.com","13_Suman_Prakash_Nair.txt","Suman Prakash Nair")]
    [InlineData("SUMAN | Tech and Product Leader\nsumansahai@example.com","14_Suman_Sahai.txt","SUMAN")]
    public async Task HeaderNamesHandleColumnsInitialsAndDecorations(string source,string file,string expected)
    {
        Assert.Equal(expected,(await Parse(source,file)).Facts.FullName);
    }

    [Fact]
    public async Task TitleCompanyAndFilenameAloneCannotSupplyPersonIdentity()
    {
        var result=await Parse("Technology Leader\nCONTACT INFORMATION\nEnterprise architecture and solution design\nexample@example.com","10_Unrelated_Person.txt");
        Assert.Empty(result.Facts.FullName);
    }

    [Theory]
    [InlineData("32_Ravi_Kumar.doc","Ravi Kumar")]
    [InlineData("21_Wg_Cdr_Dr_Arjun_Kapoor_Retd.docx","Arjun Kapoor")]
    [InlineData("25_Mohan_Sharma_Ph_D.docx","Mohan Sharma")]
    [InlineData("Technology_Leader.pdf","")]
    public void FilenameSuggestionsAreCleanedButRemainSeparateFromParsedFacts(string file,string expected)
    {
        Assert.Equal(expected,ResumeParsingService.FilenameNameHint(file));
    }

    [Theory]
    [InlineData("Location: Pune, India | | Strategic technology leader with twenty years of experience","Pune, India")]
    [InlineData("Pune, India\nravi@example.com","Pune, India")]
    [InlineData("Current location: Acme Technologies Limited","")]
    [InlineData("Current location: Development Center Oracle R&D","")]
    public async Task ContactLocationDoesNotIncludeAdjacentColumnsOrEmployers(string contact,string expected)
    {
        Assert.Equal(expected,(await Parse("RAVI KUMAR\n"+contact,"Ravi_Kumar.txt")).Facts.CurrentLocation);
    }
}
