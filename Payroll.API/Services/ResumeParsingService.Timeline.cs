using System.Text.RegularExpressions;

namespace Payroll.API.Services;

public sealed partial class ResumeParsingService
{
    private static bool IsResponsibility(string value)=>Regex.IsMatch(value,
        @"^(?:[|•·●▸\uF0B7\uF075]\s*)?(?:led|built|directed|architected|developed|drove|served|handled|worked|contributed|implemented|boosting|heading|spearheaded|scaled|responsible|progressed|managed|challenged|defined|influenced|grew|seeking|reported|acted|established|designed|delivered|achieved|conceptualized|transformed|partnered|owns?|appointed|recruited|entrusted|as\s+(?:a|an|the)|complete redesigning)\b|\b(?:\d+\+?\s+years|responsible for|reduced annual|to produce|for planning|requirements and|across \d+|entrusted with)\b",RegexOptions.IgnoreCase);

    private static bool IsRoleHeader(string value)=>value.Length is >=3 and <=180&&RoleTitlePattern.IsMatch(value)
        &&!IsResponsibility(value)&&SectionCode(value) is null
        &&!Regex.IsMatch(value,@"\b(?:reports?|reporting)\s+to\b|\bteam\s+size\b",RegexOptions.IgnoreCase)
        &&!Regex.IsMatch(value,@"^(?:key\s+)?roles?\s*(?:&|and)?\s*responsibilities\b",RegexOptions.IgnoreCase)
        &&!Regex.IsMatch(value,@"^(?:with|for|as|and|including)\b",RegexOptions.IgnoreCase)
        &&!Regex.IsMatch(value,@"^[|•·●▸\uF0B7\uF075]|\b(?:led|managed|built|partnered|worked|designed|spearheaded|responsible)\b",RegexOptions.IgnoreCase)
        &&!EmailPattern.IsMatch(value)&&!Regex.IsMatch(value,@"https?://|www\.|linkedin|github|[.!?]$",RegexOptions.IgnoreCase)
        &&value.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=24;

    private static string CleanEmploymentMetadata(string value) => Regex.Replace(value,
        @"(?:^|\s+)(?:[|I(]\s*)?(?:reports?|reporting)\s+to\b.*$", "", RegexOptions.IgnoreCase).Trim().TrimEnd(' ', '(', '[');

    private static bool IsEmployerHeader(string value) => value.Length is >=2 and <=180
        && value.Any(char.IsLetter) && !IsResponsibility(value) && SectionCode(value) is null
        && !IsExperienceTableHeader(value)
        && !IsRoleHeader(value) && !EmailPattern.IsMatch(value)
        && !Regex.IsMatch(value,@"https?://|www\.|linkedin|\b(?:orchestrated|spearheaded|responsible|reporting)\b",RegexOptions.IgnoreCase)
        && !FlexibleEmploymentDateRangePattern.IsMatch(value)
        && !Regex.IsMatch(value,@"^[|•·●▸\uF0B7\uF075]|^leadership\s+in\b|\b(?:built|managed|established)\b|^(?:full.time|part.time|deputed)\b|^(?:Global|India|Technology Products|Core Business Areas)$",RegexOptions.IgnoreCase)
        && value.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=16
        && (!Regex.IsMatch(value,@"[.!?]$") || Regex.IsMatch(value,@"\b(?:Ltd|Inc|Co|Corp|Pvt)\.$",RegexOptions.IgnoreCase));

    private static bool HasOrganizationMarker(string value) => Regex.IsMatch(value,
        @"\b(?:Ltd|Limited|Pvt|Private|Inc|Corporation|Company|Bank|Systems|Services|Technologies|University|Institute|Division|Government|Group|Labs|Research|Air Force|UIDAI|NBFC)\b", RegexOptions.IgnoreCase);

    private static ResumeParsedExperience? ExplicitCurrentRole(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim(' ', '|', '•', '·', '\uF0A7', '\uF0D8');
            var label = Regex.Match(line, @"^(?:current|present)\s+(?:role|position|profile|designation)\b[ \t]*[:\-]?[ \t]*(?<value>.*)$", RegexOptions.IgnoreCase);
            if (!label.Success) continue;
            var value = label.Groups["value"].Value.Trim(' ', '-', '–', '—');
            if (value.Length == 0 && i + 1 < lines.Length) value = lines[i + 1].Trim();
            value = Regex.Replace(value, @"^working\s+as\s+", "", RegexOptions.IgnoreCase);
            value = Regex.Split(value, @"[,;]\s*(?:providing|leading|responsible|on\s+deputation)\b", RegexOptions.IgnoreCase)[0];
            ParseRoleCompanyLocation(value, out var title, out var company, out var location);
            company = EmployerCaption(company);
            if (IsRoleHeader(title) && IsEmployerHeader(company))
                return new(company, title, location, null, null, true, line);
        }
        return null;
    }

    private static string EmployerCaption(string value)
    {
        var clean = Regex.Replace(Regex.Split(value, @"\s*\|\s*")[0].Trim(), @"\s*\((?:From)?\s*$|\s*\[\s*$", "", RegexOptions.IgnoreCase);
        var parts = clean.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && !HasOrganizationMarker(parts[0]))
            clean = parts.Skip(1).FirstOrDefault(HasOrganizationMarker) ?? clean;
        return CleanField(clean, 180);
    }

    private static string NearbyEmployer(List<string> lines, int index)
    {
        foreach (var offset in new[] { -1, 1, -2, 2 })
        {
            var at = index + offset;
            if (at < 0 || at >= lines.Count) continue;
            if (Math.Abs(offset) == 2 && FlexibleEmploymentDateRangePattern.IsMatch(lines[index + Math.Sign(offset)])) continue;
            var raw = Regex.Split(lines[at], @"\s*\|\s*")[0].Trim();
            raw = Regex.Replace(raw, @"^(?:current\s+)?(?:organi[sz]ation|company|employer)\s*:\s*", "", RegexOptions.IgnoreCase);
            var parts = Regex.Split(lines[at], @"\s*\|\s*");
            var hasPlace = parts.Length > 1 && parts.Skip(1).Any(part => PlaceEvidence.IsMatch(part));
            if (IsEmployerHeader(raw) && (HasOrganizationMarker(raw) || hasPlace)) return CleanField(raw, 180);
        }
        return "";
    }

    private static bool TryTimelineHeader(List<string> lines,int dateIndex,out string title,out string company,out string location, bool allowCompanyDescription = false)
    {
        title=company=location="";
        var titles=new List<string>();
        for(var index=dateIndex+1;index<Math.Min(lines.Count,dateIndex+5);index++)
        {
            var line=lines[index].Trim();
            if(SectionCode(line) is not null||FlexibleEmploymentDateRangePattern.IsMatch(line)
                ||line.StartsWith('|')||line.StartsWith('•'))break;
            if (IsResponsibility(line))
            {
                // A one-line company description can precede the actual role.
                // Skip it only when the immediately following line is a role;
                // never scan through responsibility bullets to an older job.
                if (allowCompanyDescription && titles.Count == 0 && index <= dateIndex + 2 && index + 1 < lines.Count
                    && IsRoleHeader(CleanEmploymentMetadata(lines[index + 1]))) continue;
                break;
            }
            foreach(var raw in Regex.Split(line,@"\s*[|•·\t]\s*"))
            {
                var part=CleanField(CleanEmploymentMetadata(raw),180);
                if(part.Length==0)continue;
                if(IsRoleHeader(part))
                {
                    // Preserve the existing "title - employer" and "title at employer"
                    // formats inside a date-first timeline as well as pipe co-titles.
                    ParseRoleCompanyLocation(part,out var role,out var employer,out _);
                    titles.Add(role);
                    if(company.Length==0&&employer.Length>0)company=employer;
                    continue;
                }
                var place=ContactLocationValue(part);
                if(place.Length>0&&PlaceEvidence.IsMatch(place))
                {
                    location=location.Length==0?place:CleanField(location+", "+place,180);
                    continue;
                }
                if(company.Length==0&&IsEmployerHeader(part))company=part;
            }
        }
        if(titles.Count==0)return false;
        title=CleanField(string.Join(" | ",titles.Distinct(StringComparer.OrdinalIgnoreCase)),180);
        return true;
    }

    private static bool IsDegreeEvidence(string line)=>!Regex.IsMatch(line,
        @"\b(?:LLM\s*(?:&|and|/|,)\s*SLM|(?:large|small) language models?|LLM.*(?:design|development|framework|AI|model|agent|regulatory)|(?:regulatory|based|house|enterprise)\s+LLM)\b",RegexOptions.IgnoreCase)
        ||Regex.IsMatch(line,@"\b(?:LL\.M\.|LLM\s+(?:in\s+)?laws?|master(?:s|'s)?\s+of\s+laws?)",RegexOptions.IgnoreCase);

    internal static bool IsVerifiedDegree(string line)
    {
        // Technical responsibilities containing a degree-like acronym are not education.
        if (IsResponsibility(line) && !Regex.IsMatch(line, @"\b(?:graduated|degree|university|institute|bachelor|master|doctorate)\b", RegexOptions.IgnoreCase)) return false;
        foreach (Match match in QualificationPattern.Matches(line))
        {
            var tail = line[match.Index..];
            if (Regex.IsMatch(line, @"^(?:certifications?|training|recognition)\s*[:.]", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(match.Value, @"^master(?:s|'s)?$", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(tail, @"^master(?:s|'s)?\s+(?:of|in|degree|computer|engineering|science|business|technology)\b", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(match.Value, @"^(?:ME|MS|BE)$")
                && !Regex.IsMatch(tail, @"^(?:ME|MS|BE)\s*(?:[|,(.\-]|in\b|of\b|degree\b|computer\b|electronics\b|electrical\b|telecom\b)", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(tail, @"^(?:master\s*(?:class|data|plan|services?|node)|MS\s+(?:Teams|Office|SQL|Dynamics)|MBA\s+Intern)\b", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(match.Value, @"^ll\.?\s*m$", RegexOptions.IgnoreCase)
                && (!IsDegreeEvidence(line) || !Regex.IsMatch(line, @"\bLL\.M\b|\b(?:laws?|university|college|degree)\b", RegexOptions.IgnoreCase))) continue;
            return true;
        }
        return false;
    }

    private static int DegreeRank(string line) => Regex.IsMatch(line, @"\b(?:ph\.?\s*d|doctorate)\b", RegexOptions.IgnoreCase) ? 80
        : Regex.IsMatch(line, @"\b(?:mba|mca|m\.?\s*tech|m\.?\s*sc|m\.?\s*com|m\.\s*[es]|MS\b|ME\b|master|ll\.?\s*m)\b", RegexOptions.IgnoreCase) ? 60
        : Regex.IsMatch(line, @"\b(?:post\s*graduate|PGD[A-Z]*)\b", RegexOptions.IgnoreCase) ? 50
        : Regex.IsMatch(line, @"\b(?:bachelor|b\.?\s*tech|b\.?\s*sc|b\.?\s*com|b\.\s*e|BE\b|bca|bba|ll\.?\s*b)\b", RegexOptions.IgnoreCase) ? 40 : 20;

    private static IEnumerable<string> ReadSkillLines(string text)
    {
        string current="";bool bullet=false;
        foreach(var raw in text.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            var line=raw.Trim();
            var nextBullet=line.StartsWith('|')||line.StartsWith('•')||line.StartsWith('·');
            if(current.Length>0&&bullet&&!nextBullet&&raw.Length>0&&char.IsWhiteSpace(raw[0])){current+=" "+line;continue;}
            if(current.Length>0)yield return current;
            current=line;bullet=nextBullet;
        }
        if(current.Length>0)yield return current;
    }
}
