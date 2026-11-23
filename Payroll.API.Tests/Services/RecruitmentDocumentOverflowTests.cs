using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;
using PdfSharp.Pdf.IO;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentDocumentOverflowTests
{
    private static string Body(RecruitmentDocumentLayout layout) => LayoutDocumentPdfService.Marker
        + JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Overflow_identifies_the_input_and_actual_page_without_disclosing_employee_values()
    {
        var layout = new RecruitmentDocumentLayout
        {
            Inputs = [new() { Key = "positionTitle", Label = "Designation" }],
            Sections =
            [
                new() { Elements = [new() { X = 65.1, Y = 150, Width = 300, Height = 40, Text = "First page" }] },
                new() { Elements = [new() { X = 65.1, Y = 148.88, Width = 80, Height = 14, Runs = [new() { Text = "{{positionTitle}}", Bold = true }] }] }
            ]
        };
        var privateValue = "Synthetic confidential research position with extended specialist responsibilities";
        var values = new Dictionary<string, string> { ["positionTitle"] = privateValue };

        var result = new LayoutDocumentPdfService().Create(Body(layout), values, draft: false);

        Assert.Null(result.Bytes);
        Assert.Contains("Document field 'Designation' on page 2", result.Error);
        Assert.Contains("80 × 14 points at (65.1, 148.88)", result.Error);
        Assert.Contains("Offer Letter template", result.Error);
        Assert.Contains("no text was omitted", result.Error);
        Assert.DoesNotContain(privateValue, result.Error);
        Assert.Equal(privateValue, values["positionTitle"]);
    }

    [Fact]
    public void Static_template_overflow_does_not_echo_the_document_clause()
    {
        const string clause = "A private contractual clause which does not belong in an error message";
        var layout = new RecruitmentDocumentLayout
        {
            Sections = [new() { Elements = [new() { X = 65.1, Y = 150, Width = 30, Height = 10, Text = clause }] }]
        };

        var result = new LayoutDocumentPdfService().Create(Body(layout), new Dictionary<string, string>(), draft: false);

        Assert.Null(result.Bytes);
        Assert.StartsWith("Template text on page 1", result.Error);
        Assert.DoesNotContain(clause, result.Error);
    }

    [Fact]
    public void Configured_minimum_font_fits_a_short_field_without_changing_its_value()
    {
        const string text = "Fixed reference";
        var field = new DocumentElement { X = 65, Y = 150, Width = 250, Height = 12, FontSize = 10.5, LineHeight = 13.2, MinFontSize = 8, Text = "{{offerNumber}}" };
        var layout = new RecruitmentDocumentLayout { Sections = [new() { Elements = [field] }] };
        var values = new Dictionary<string, string> { ["offerNumber"] = text };

        var result = new LayoutDocumentPdfService().Create(Body(layout), values, draft: false);

        Assert.Equal("", result.Error);
        Assert.NotNull(result.Bytes);
        using var pdf = PdfReader.Open(new MemoryStream(result.Bytes!), PdfDocumentOpenMode.Import);
        Assert.Single(pdf.Pages);
        Assert.Equal(text, values["offerNumber"]);
        Assert.Equal(10.5, field.FontSize);
    }

    [Fact]
    public void Minimum_font_is_a_floor_and_does_not_allow_unreadable_or_clipped_letters()
    {
        var layout = new RecruitmentDocumentLayout
        {
            Inputs = [new() { Key = "offerNumber", Label = "Offer reference" }],
            Header = [new() { X = 65, Y = 30, Width = 25, Height = 10, MinFontSize = 6, Text = "{{offerNumber}}" }],
            Sections = [new()]
        };
        var result = new LayoutDocumentPdfService().Create(Body(layout), new Dictionary<string, string> { ["offerNumber"] = new string('W', 160) }, draft: false);

        Assert.Null(result.Bytes);
        Assert.Contains("Document field 'Offer reference' on page 1", result.Error);
        Assert.Contains("no text was omitted", result.Error);
    }

    [Fact]
    public void A_split_endorsement_clause_can_wrap_for_a_long_site_without_changing_its_words_or_font()
    {
        const string first = "I understand that I am not a permanent or temporary employee of {{institutionShortName}}, {{institutionLocation}} and that my";
        const string second = "engagement is purely on time-bound contractual basis through Synthetic Employer Private Limited.";
        var elements = new List<DocumentElement>
        {
            new() { X = 65.1, Y = 148.876, Width = 465.7, Height = 14.7, FontSize = 10.5, LineHeight = 12.6, MinFontSize = 9, Text = first },
            new() { X = 65.1, Y = 162.076, Width = 465.7, Height = 14.7, FontSize = 10.5, LineHeight = 12.6, MinFontSize = 9, Text = second },
            new() { X = 65.1, Y = 179.276, Width = 465.7, Height = 14.7, FontSize = 10.5, LineHeight = 12.6, MinFontSize = 9, Text = "Following undertaking" }
        };
        var layout = new RecruitmentDocumentLayout { Sections = [new() { Elements = elements }] };
        var values = new Dictionary<string, string> { ["institutionShortName"] = "Example Institute", ["institutionLocation"] = "Mumbai" };
        var renderer = new LayoutDocumentPdfService();
        Assert.NotNull(renderer.Create(Body(layout), values, draft: false).Bytes);

        values["institutionShortName"] = "Example Institute - Bone Health Clinic, Dadar East";
        Assert.Null(renderer.Create(Body(layout), values, draft: false).Bytes);
        var oldText = string.Join(" ", elements.Select(e => e.Text));
        elements[0].Text = first + " " + second;
        elements[0].Height = 38;
        elements.RemoveAt(1);
        elements[1].Y += 10.1;
        var result = renderer.Create(Body(layout), values, draft: false);

        Assert.NotNull(result.Bytes);
        Assert.Equal("", result.Error);
        Assert.Equal(oldText, string.Join(" ", elements.Select(e => e.Text)));
        Assert.All(elements, element => Assert.Equal(10.5, element.FontSize));
        Assert.True(elements[0].Y + elements[0].Height < elements[1].Y);
        Assert.Equal("Example Institute - Bone Health Clinic, Dadar East", values["institutionShortName"]);
    }
}
