using System.Text.RegularExpressions;

namespace Mcs.Trace;

/// <summary>
///   Reads <c>docs/requirements.md</c>: the table, the section under each row, and the
///   <c>evidence:</c> markers inside those sections.
/// </summary>
/// <remarks>
///   <para>
///     Parsed by position rather than by column heading. The ID column's heading is deliberately
///     empty -- <c>| | Requirement, in short | Type | Method | Status |</c> -- so a parser that
///     looks up "ID" finds nothing, and one that tolerates that by guessing is worse. The shape is
///     asserted instead: five columns, in that order, or the file is malformed and this fails
///     saying which line.
///   </para>
///   <para>
///     A markdown parser is not what this is and must not become one. It knows exactly three
///     constructs: a pipe table, an <c>## MCS-NNN</c> heading, and an inline code span beginning
///     <c>evidence:</c>. Anything else in the document is prose it steps over.
///   </para>
/// </remarks>
internal static class RequirementsTable
{
    //  The ID cell as the table writes it. The anchor is checked against the label rather than
    //  ignored: [MCS-004](#mcs-003) is a link that works, lands on the wrong requirement, and is
    //  invisible to every other check here.
    private static readonly Regex IdCell = new(
        @"^\[(?<id>MCS-(?<number>\d{3}))\]\(#(?<anchor>[a-z0-9-]+)\)$", RegexOptions.Compiled);

    private static readonly Regex Heading = new(
        @"^##\s+(?<id>MCS-\d{3})\s*$", RegexOptions.Compiled);

    //  The marker lives inside an inline code span, which is why the backticks are part of the
    //  pattern rather than trimmed afterwards: `evidence: notes/latency-at-twelve.md`. Requiring
    //  them means the word "evidence:" in ordinary prose -- and the file's own **Evidence.**
    //  paragraph headings -- are not mistaken for links.
    private static readonly Regex EvidenceSpan = new(
        @"`evidence:\s*(?<target>[^`]+?)\s*`", RegexOptions.Compiled);

    private static readonly Regex RemovedIdCell = new(
        @"^\**(?<id>MCS-\d{3})\**$", RegexOptions.Compiled);

    /// <summary>Everything the checks need out of the document.</summary>
    internal sealed record Document(
        IReadOnlyList<Requirement> Requirements,
        IReadOnlySet<string> RemovedIds);

    internal static Document Read(string path)
    {
        string[] lines = File.ReadAllLines(path);

        List<Requirement> requirements = [];
        foreach ((string[] cells, int line) in TableRowsUnder(lines, "## The table"))
        {
            requirements.Add(ParseRow(cells, line, path));
        }

        if (requirements.Count == 0)
        {
            throw new FormatException($"{path}: found no requirement rows under '## The table'.");
        }

        Dictionary<string, (IReadOnlyList<EvidenceLink> Evidence, int Line)> sections =
            ReadSections(lines);

        List<Requirement> joined = [];
        foreach (Requirement requirement in requirements)
        {
            if (!sections.TryGetValue(requirement.Id, out var section))
            {
                throw new FormatException(
                    $"{path}({requirement.Line}): {requirement.Id} is in the table with no "
                    + $"'## {requirement.Id}' section beneath it.");
            }

            joined.Add(requirement with { Evidence = section.Evidence });
        }

        HashSet<string> removed = [];
        foreach ((string[] cells, int line) in TableRowsUnder(lines, "## Removed"))
        {
            Match match = RemovedIdCell.Match(cells[0]);
            if (match.Success)
            {
                removed.Add(match.Groups["id"].Value);
            }
            else if (cells[0] is not ("—" or "-" or ""))
            {
                throw new FormatException(
                    $"{path}({line}): the removed table's first cell is '{cells[0]}', which is "
                    + "neither a requirement id nor the em-dash placeholder.");
            }
        }

        return new Document(joined, removed);
    }

    private static Requirement ParseRow(string[] cells, int line, string path)
    {
        if (cells.Length != 5)
        {
            throw new FormatException(
                $"{path}({line}): expected 5 columns (id, summary, type, method, status), "
                + $"found {cells.Length}.");
        }

        Match id = IdCell.Match(cells[0]);
        if (!id.Success)
        {
            throw new FormatException(
                $"{path}({line}): '{cells[0]}' is not an id cell of the form [MCS-001](#mcs-001).");
        }

        string requirementId = id.Groups["id"].Value;
        string anchor = id.Groups["anchor"].Value;
        if (!anchor.Equals(requirementId, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"{path}({line}): {requirementId} links to #{anchor}, which is another "
                + "requirement's section.");
        }

        return new Requirement(
            Id: requirementId,
            Number: int.Parse(id.Groups["number"].Value),
            Summary: cells[1],
            Methods: ParseMethods(cells[3], line, path),
            Status: ParseStatus(cells[4], line, path, out string? reason),
            StatusReason: reason,
            Evidence: [],
            Line: line);
    }

    private static IReadOnlyList<VerificationMethod> ParseMethods(string cell, int line, string path)
    {
        List<VerificationMethod> methods = [];
        foreach (string part in cell.Split('+', StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse(part, ignoreCase: false, out VerificationMethod method))
            {
                throw new FormatException(
                    $"{path}({line}): '{part}' is not one of Test, Inspection, Demonstration, "
                    + "Analysis. The four are defined in the document's own 'How to read it'.");
            }

            methods.Add(method);
        }

        return methods;
    }

    private static VerificationStatus ParseStatus(
        string cell, int line, string path, out string? reason)
    {
        //  Longest first, and not a Contains check: "not verified" contains "verified", so a
        //  shortest-match parser reads every unverified row as verified -- silently, and in the
        //  direction that makes the table look better than it is.
        string text = cell.Replace("**", string.Empty).Trim();
        (string Prefix, VerificationStatus Status)[] candidates =
        [
            ("partly verified", VerificationStatus.PartlyVerified),
            ("not verified", VerificationStatus.NotVerified),
            ("verified", VerificationStatus.Verified),
        ];

        foreach ((string prefix, VerificationStatus status) in candidates)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = text[prefix.Length..].Trim();
            //  The reason is what follows the em dash. An empty rest is a bare status, which is
            //  only allowed for a plain "verified" -- see Program's per-status rules.
            reason = rest.StartsWith('—') ? rest.TrimStart('—').Trim() : null;
            if (rest.Length > 0 && reason is null)
            {
                throw new FormatException(
                    $"{path}({line}): status '{text}' has trailing text that is not an em-dash "
                    + "clause; a qualifier is written '— why'.");
            }

            return status;
        }

        throw new FormatException(
            $"{path}({line}): status '{text}' is not one of verified, partly verified, "
            + "not verified.");
    }

    /// <summary>Rows of the first pipe table appearing after <paramref name="heading"/>.</summary>
    private static IEnumerable<(string[] Cells, int Line)> TableRowsUnder(
        string[] lines, string heading)
    {
        int start = Array.FindIndex(
            lines, l => l.TrimEnd().Equals(heading, StringComparison.Ordinal));
        if (start < 0)
        {
            yield break;
        }

        bool inTable = false;
        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                yield break;
            }

            if (!line.StartsWith('|'))
            {
                if (inTable)
                {
                    yield break;
                }

                continue;
            }

            string[] cells = SplitRow(line);

            //  The header and its --- separator are the two rows a pipe table has before its
            //  data. Recognising the separator by content rather than by counting rows means a
            //  table that grows a second header line does not silently shift by one.
            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
            {
                inTable = true;
                continue;
            }

            if (inTable)
            {
                yield return (cells, i + 1);
            }
        }
    }

    private static string[] SplitRow(string line)
    {
        string trimmed = line.Trim();
        //  A leading and trailing pipe produce an empty cell at each end that is not a column.
        //  Trimming exactly one from each side rather than using RemoveEmptyEntries keeps the
        //  requirements table's genuinely empty first heading cell where it belongs.
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static Dictionary<string, (IReadOnlyList<EvidenceLink>, int)> ReadSections(
        string[] lines)
    {
        Dictionary<string, (IReadOnlyList<EvidenceLink>, int)> sections = [];

        string? current = null;
        int currentLine = 0;
        List<EvidenceLink> evidence = [];

        void Close()
        {
            if (current is not null)
            {
                sections[current] = (evidence, currentLine);
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            Match heading = Heading.Match(lines[i].TrimEnd());
            if (heading.Success)
            {
                Close();
                current = heading.Groups["id"].Value;
                currentLine = i + 1;
                evidence = [];
                continue;
            }

            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                current = null;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            foreach (Match match in EvidenceSpan.Matches(lines[i]))
            {
                evidence.Add(new EvidenceLink(match.Groups["target"].Value, i + 1));
            }
        }

        Close();
        return sections;
    }
}
