using System.Text.RegularExpressions;

namespace Payroll.API.Services;

public sealed partial class ResumeParsingService
{
    private static readonly Regex NonPersonHeading = new(@"\b(?:technology|technologies|technical|tech|product|enterprise|architecture|solution|design|leadership|leader|executive|professional|experience|contact|information|details|curr?icul[au]m|vitae|resume|summary|competencies|skills|education|academic|university|college|company|limited|private|inc|corporation|strategic|strategy|cloud|digital|transformation|engineer|engineering|services|systems|development|center|centre)\b", RegexOptions.IgnoreCase|RegexOptions.Compiled);
    private static readonly Regex PersonTokens = new(@"^\p{L}[\p{L}\p{M}.'-]*(?:\s+\p{L}[\p{L}\p{M}.'-]*){1,5}$", RegexOptions.Compiled);
    private static readonly Regex OrganizationOrProse = new(@"\b(?:pvt|ltd|limited|inc|corp|corporation|company|technologies|technology|university|college|institute|center|centre|leadership|strategy|strategic|transformation|competencies|experience|academic|architecture|innovation|customers|partners|partnerships|leading|building|driving|develop|include|selected|impact|organization|organisation|revenue|optimization|modernized|engineering|commerce|product|cloud|platforms|software|enablement|towers|linguistic)\b", RegexOptions.IgnoreCase|RegexOptions.Compiled);
    private static readonly Regex PlaceEvidence = new(@"\b(?:India|Bengaluru|Bangalore|Mumbai|Delhi|Noida|Pune|Hyderabad|Chennai|Kolkata|Gurugram|Gurgaon|Thane|Navi Mumbai|Bhopal|Chandigarh|Ahmedabad|Kochi|Lucknow|Singapore|Australia|London|Dubai|United Kingdom|United States|USA|California|Haryana|Maharashtra|Karnataka|Telangana|Tamil Nadu|Uttar Pradesh)\b", RegexOptions.IgnoreCase|RegexOptions.Compiled);

    private static string CleanPersonHeading(string value)
    {
        var clean=Regex.Replace(value,@"(?i)^\s*(?:\d+[_. -]+)?(?:resume|cv|profile|updated|latest|final)\b[:_ -]*", "");
        clean=Regex.Replace(clean,@"(?i)^\s*(?:wg[ .]+cdr|wing commander)\s+", "");
        clean=Regex.Replace(clean,@"(?i)^\s*\(?dr\.?\)?\s+", "");
        clean=Regex.Replace(clean,@"(?i)[,\s]+(?:\(?retd\.?\)?|ph[.\s]*d\.?)\s*$", "");
        return Regex.Replace(clean,@"\s+"," ").Trim(' ',',',';',':','-','–','—');
    }

    private static bool IsPersonHeading(string value)=>value.Length is >=3 and <=100
        &&PersonTokens.IsMatch(value)&&!NonPersonHeading.IsMatch(value)&&!RoleTitlePattern.IsMatch(value)
        &&SectionCode(value) is null;

    public static string FilenameNameHint(string fileName)
    {
        var hint=Path.GetFileNameWithoutExtension(fileName).Replace('_',' ');
        hint=Regex.Replace(hint,@"^\s*\d+[. -]*", "");
        hint=CleanPersonHeading(hint);
        return IsPersonHeading(hint)?hint:"";
    }

    private static string ExtractHeaderPersonName(string text,string fileName)
    {
        var labelled=CleanPersonHeading(NameLabelPattern.Match(text).Groups["value"].Value);
        if(IsPersonHeading(labelled))return labelled;
        var header=string.Join('\n',text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Take(24));
        if(header.Length>2500)header=header[..2500];
        header=Regex.Replace(header,@"[^\s|]+@[^\s|]+|https?://\S+|www\.\S+", " ",RegexOptions.IgnoreCase);
        var hint=FilenameNameHint(fileName);
        // Filenames help reconnect interleaved PDF columns, but every name token
        // must occur in document header text, in order. An email is not a surname.
        if(IsPersonHeading(hint))
        {
            var offset=0;var parts=new List<string>();
            foreach(var token in hint.Split(' ',StringSplitOptions.RemoveEmptyEntries))
            {
                var match=Regex.Match(header[offset..],@"(?<!\p{L})"+Regex.Escape(token.Trim('.'))+@"\.?(?!\p{L})",RegexOptions.IgnoreCase);
                if(!match.Success){parts.Clear();break;}
                parts.Add(match.Value.TrimEnd('.'));offset+=match.Index+match.Length;
            }
            if(parts.Count>=2)return string.Join(' ',parts);
        }
        string singleName="";
        foreach(var line in header.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            var withoutContact=EmailPattern.Replace(PhonePattern.Replace(line," ")," ");
            foreach(var part in Regex.Split(withoutContact,@"[|•\t]|\s+[–—-]\s+"))
            {
                var candidate=CleanPersonHeading(part);
                if(IsPersonHeading(candidate))return candidate;
                if(singleName.Length==0&&Regex.IsMatch(candidate,@"^\p{L}[\p{L}\p{M}'-]{2,}$")
                    &&hint.Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Equals(candidate,StringComparison.OrdinalIgnoreCase)==true
                    &&!NonPersonHeading.IsMatch(candidate))singleName=candidate;
            }
            if(singleName.Length>0)return singleName;
        }
        return singleName;
    }

    private static string ContactLocationValue(string value)
    {
        var clean=Regex.Split(value,@"[|•\t]|\b(?:Email|Phone|Mobile|Linguistic Ability|Languages?)\s*:",RegexOptions.IgnoreCase)[0];
        clean=Regex.Replace(clean,@"(?i)^\s*(?:(?:current|permanent|residential|postal|mailing)\s+)?(?:address|location)\s*[:\-]\s*","");
        clean=CleanLocation(clean);
        var phone=PhonePattern.Match(clean);
        if(phone.Success)clean=clean[..phone.Index].TrimEnd(' ','I','1','|',',').TrimStart();
        clean=Regex.Replace(clean,@"^[^\p{L}\d]+","");
        return clean.Length is >=2 and <=180&&!OrganizationOrProse.IsMatch(clean)
            &&!RoleTitlePattern.IsMatch(clean)&&!EmailPattern.IsMatch(clean)
            &&clean.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=20?clean:"";
    }

    private static string ExtractContactLocation(string text,string address,string? roleLocation)
    {
        var explicitLocation=ContactLocationValue(LocationLabelPattern.Match(text).Groups["value"].Value);
        if(explicitLocation.Length>0)return explicitLocation;
        var contactAddress=ContactLocationValue(address);
        if(contactAddress.Length>0)return contactAddress;
        foreach(var line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Take(12))
        foreach(var part in Regex.Split(line,@"[|•\t]"))
        {
            var candidate=ContactLocationValue(part);
            if(candidate.Length>0&&candidate.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=9
                &&PlaceEvidence.IsMatch(candidate)&&!Regex.IsMatch(candidate,@"https?://|www\.|linkedin|\b(?:with|years|across|at|for|of)\b",RegexOptions.IgnoreCase))return candidate;
        }
        var role=ContactLocationValue(roleLocation??"");
        return PlaceEvidence.IsMatch(role)?role:"";
    }
}
