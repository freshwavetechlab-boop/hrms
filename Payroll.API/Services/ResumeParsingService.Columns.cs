using System.Text.RegularExpressions;

namespace Payroll.API.Services;

public sealed partial class ResumeParsingService
{
    // Only separate independently headed sidebars. Aligned experience tables
    // retain their row order and pipe separators.
    internal static string RestoreIndependentPdfColumns(string text)
    {
        var pages=text.Replace("\r\n","\n").Split('\f');
        var first=pages[0].Split('\n');
        var boundary=ColumnBoundary(first);
        if(boundary is null)return text;
        var firstLeft=first.Select(line=>ColumnPart(line,boundary.Value,false)).ToList();
        var firstRight=first.Select(line=>ColumnPart(line,boundary.Value,true)).ToList();
        var leftHeading=firstLeft.FindIndex(line=>SectionCode(line) is not null);
        var rightHeading=firstRight.FindIndex(line=>SectionCode(line) is not null);
        var explicitTimeline = firstLeft.Concat(firstRight).Any(line=>SectionCode(line)=="EXPERIENCE");
        var datedMain = firstLeft.Count(line=>FlexibleEmploymentDateRangePattern.IsMatch(line))>=2
            ||firstRight.Count(line=>FlexibleEmploymentDateRangePattern.IsMatch(line))>=2;
        var structuredSidebar = new[] { firstLeft, firstRight }.Any(side => side.Any(line => SectionCode(line) == "EDUCATION")
            && side.Any(line => SectionCode(line) == "SKILLS"));
        datedMain |= structuredSidebar && firstLeft.Concat(firstRight).Any(line => FlexibleEmploymentDateRangePattern.IsMatch(line));
        if(leftHeading<0||rightHeading<0
            ||!explicitTimeline&&!datedMain
            ||Math.Max(firstLeft.Count(line=>SectionCode(line) is not null),firstRight.Count(line=>SectionCode(line) is not null))<2)return text;
        var headerBlocks = new[] { firstLeft.Take(leftHeading).ToList(), firstRight.Take(rightHeading).ToList() }
            .OrderByDescending(block => block.Any(line => EmailPattern.IsMatch(line) || PhonePattern.IsMatch(line)));
        var headers=headerBlocks.SelectMany(RepairColumnEmailWraps).Where(line=>line.Length>0).ToList();
        var left=firstLeft.Skip(leftHeading).ToList();
        var right=firstRight.Skip(rightHeading).ToList();
        var tail=new List<string>();
        var fullWidth=false;
        foreach(var page in pages.Skip(1))
        {
            var lines=page.Split('\n');
            var next=fullWidth?null:ColumnBoundary(lines,boundary);
            if(next is null){fullWidth=true;tail.Add(page);continue;}
            left.AddRange(lines.Select(line=>ColumnPart(line,next.Value,false)));
            right.AddRange(lines.Select(line=>ColumnPart(line,next.Value,true)));
        }
        // This only reorders copied text. No title, employer or skill is supplied
        // by filename, JD, model reference or another candidate.
        return string.Join('\n',headers.Concat(left).Concat(right).Concat(tail));
    }

    private static IEnumerable<string> RepairColumnEmailWraps(IEnumerable<string> source)
    {
        var lines=source.ToList();
        for(var index=0;index<lines.Count;index++)
        {
            var token=lines[index].Trim();
            if(index+1<lines.Count
                &&Regex.IsMatch(token,@"^[A-Z0-9._%+\-]+@[A-Z0-9\-]+$",RegexOptions.IgnoreCase)
                &&Regex.IsMatch(lines[index+1].Trim(),@"^[A-Z0-9\-]{1,12}\.[A-Z]{2,10}$",RegexOptions.IgnoreCase))
            {
                // Both fragments must be token-only and in the same independently
                // detected column. Never concatenate unrelated contact rows globally.
                yield return token+lines[++index].Trim();
            }
            else yield return lines[index];
        }
    }

    private static string ColumnPart(string line,int boundary,bool right)=>
        (right?line.Length>boundary?line[boundary..]:"":line[..Math.Min(line.Length,boundary)]).TrimEnd();

    private static int? ColumnBoundary(string[] lines,int? previous=null)
    {
        var starts=lines.Where(line=>line.Length<=1000)
            .SelectMany(line=>Regex.Matches(line,@"(?<=\S) {3,}(?=\S)").Cast<Match>())
            .Select(match=>match.Index+match.Length)
            .Where(position=>position is >=20 and <=160&&(!previous.HasValue||Math.Abs(position-previous.Value)<=16)).ToList();
        if(starts.Count<6)return null;
        var center=starts.Distinct().OrderByDescending(value=>starts.Count(position=>Math.Abs(position-value)<=3)).First();
        // Body bullets can be indented several columns beyond the sidebar heading.
        // Include that nearby gutter edge so PERSONAL DETAILS/SKILLS are not cut.
        var boundary=starts.Where(value=>Math.Abs(value-center)<=10).Min();
        var dual=lines.Count(line=>line.Length>boundary&&line[..boundary].Any(char.IsLetter)&&line[boundary..].Any(char.IsLetter));
        return dual>=6?boundary:null;
    }
}
