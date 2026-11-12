using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

// A neutral starting layout, not a client contract or a source of payroll rules.
// Client and organization branding are supplied by the existing renderer.
internal static class DefaultRecruitmentOfferTemplate
{
    internal const string Code = "AUTO_BULK_OFFER_V1";
    internal const string Name = "Standard employee-list offer";

    internal static string Body()
    {
        var layout = new RecruitmentDocumentLayout
        {
            Title = "Offer Letter - {{candidateName}}",
            Inputs =
            [
                Input("candidateName", "Employee name", true, ["Employee Name", "Candidate Name", "Full Name", "Name"]),
                Input("candidateEmail", "Email", false, ["Email", "Email ID", "Email Address", "Work Email"]),
                Input("candidatePhone", "Mobile", false, ["Mobile", "Mobile No", "Phone", "Contact Number"]),
                Input("employeeCode", "Employee code", false, ["Employee Code", "Employee ID", "Employee Number"]),
                Input("positionTitle", "Designation", true, ["Designation", "Position", "Position Title", "Job Title"]),
                Input("joiningDate", "Date of joining", true, ["Date of Joining", "Joining Date", "DOJ"], "date"),
                Input("jobLocation", "Work location", true, ["Work Location", "Location", "Place of Posting", "Institute", "Institution"]),
                Input("monthlyGross", "Monthly gross pay", true, ["Monthly Gross", "Monthly Gross Salary", "Gross Salary", "Gross Pay", "Monthly Salary", "Consolidated Salary"], "money"),
                Input("annualCtc", "Annual CTC", false, ["Annual CTC", "Annual Salary"], "money"),
                Input("department", "Department", false, ["Department", "Function"]),
                Input("candidateAddress", "Employee address", false, ["Address", "Current Address", "Permanent Address"]),
                Input("remarks", "Additional offer terms", false, ["Remarks", "Additional Terms", "Offer Terms"]),
                Input("signatoryName", "Authorized signatory", false, ["Authorized Signatory", "Signatory Name"])
            ],
            ImportPresets = [new() { Name = "Employee list", HeaderRow = 1 }],
            Header =
            [
                new() { Kind = "image", Asset = "organizationLogo", X = 55, Y = 30, Width = 55, Height = 50 },
                new() { X = 120, Y = 38, Width = 420, Height = 45, FontSize = 17, LineHeight = 20, Bold = true, Text = "{{organizationName}}" },
                new() { Kind = "line", X = 55, Y = 96, Width = 485, Height = 0 }
            ],
            Footer = [new() { X = 65.1, Y = 790, Width = 465.6, Height = 20, FontSize = 8, LineHeight = 10, Align = "center", Text = "{{organizationName}} | Offer letter" }],
            Sections =
            [
                new()
                {
                    Flow = true, Top = 115, Bottom = 755,
                    Blocks =
                    [
                        new() { Text = "OFFER LETTER", FontSize = 16, LineHeight = 20, Bold = true, Align = "center", SpaceAfter = 16 },
                        new() { Text = "Reference: {{offerNumber}}\nDate: {{offerDate}}", SpaceAfter = 14 },
                        new() { Text = "To,\n{{candidateName}}\n{{candidateAddress}}", SpaceAfter = 12 },
                        new() { Text = "Dear {{candidateName}},", SpaceAfter = 9 },
                        new() { Text = "We are pleased to offer you the position of {{positionTitle}} with {{organizationName}}, for deployment with {{clientName}}. The details of this offer are set out below.", SpaceAfter = 13 },
                        new()
                        {
                            Kind = "table", ColumnWidths = [175, 290.6], PaddingY = 5, SpaceAfter = 14,
                            Rows =
                            [
                                Row("Client", "{{clientName}}"),
                                Row("Designation", "{{positionTitle}}"),
                                Row("Work location", "{{jobLocation}}"),
                                Row("Date of joining", "{{joiningDate.date}}"),
                                Row("Monthly gross pay (INR)", "{{monthlyGross.money}}")
                            ]
                        },
                        new() { Text = "The stated amount is monthly gross pay. Applicable deductions will be shown in payroll. Any additional terms agreed for this offer are recorded below.", SpaceAfter = 10 },
                        new() { Text = "{{remarks}}", SpaceAfter = 16 },
                        new() { Text = "For {{organizationName}}", Bold = true, SpaceAfter = 5 },
                        new() { Kind = "signature", Height = 45, Text = "[Signature & Stamp]", SpaceAfter = 3 },
                        new() { Text = "{{signatoryName}}\nAuthorized Signatory", SpaceAfter = 6 }
                    ]
                }
            ]
        };
        LayoutDocumentPdfService.Validate(layout);
        return LayoutDocumentPdfService.Marker + JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static DocumentInput Input(string key, string label, bool required, List<string> headers, string type = "text") =>
        new() { Key = key, Label = label, Required = required, SourceHeaders = headers, Type = type };

    private static List<DocumentCell> Row(string label, string value) => [new() { Text = label, Bold = true }, new() { Text = value }];
}
