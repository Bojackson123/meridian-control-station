using System.Text.RegularExpressions;

namespace Mcs.Trace;

/// <summary>Everything a run produced, gathered from one directory.</summary>
/// <remarks>
///   One directory rather than a flag per input. The alternative -- naming each suite's results on
///   the command line -- makes the CI step the place where a forgotten suite becomes invisible,
///   which is precisely the failure this tool is for. Gathering by pattern and then asserting the
///   set against the solution's own list of test projects moves that check into the tool.
/// </remarks>
internal sealed record Evidence(
    IReadOnlyDictionary<string, string> Assemblies,
    IReadOnlyList<TestOutcomes.TrxRun> TrxRuns,
    IReadOnlyList<string> JUnitFiles,
    IReadOnlyList<TagSite> Tags,
    IReadOnlyList<TestResult> ManagedResults,
    IReadOnlyList<TestResult> WebResults)
{
    //  Bracketed, and matched with word boundaries either side by the brackets themselves. The
    //  console has no attributes, so a vitest tag is part of the test's title -- and a bare
    //  "MCS-002" in a title would turn every prose mention of a requirement into a claim to verify
    //  it, which is a promotion nobody made.
    private static readonly Regex WebTag = new(@"\[(?<id>MCS-\d{3})\]", RegexOptions.Compiled);

    internal static Evidence Gather(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"No evidence directory at '{directory}'. It holds the .trx files, the console's "
                + "JUnit xml and the built test assemblies; tools/trace/README.md has the run "
                + "that produces it.");
        }

        Dictionary<string, string> assemblies = VerifiesTags.FindAssemblies(directory)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        List<TagSite> tags = [];
        foreach (string path in assemblies.Values)
        {
            tags.AddRange(VerifiesTags.Read(path));
        }

        List<TestOutcomes.TrxRun> runs = [];
        foreach (string path in Directory
            .EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            runs.Add(TestOutcomes.ReadTrx(path));
        }

        List<string> junitFiles = [];
        List<TestResult> webResults = [];
        foreach (string path in Directory
            .EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            //  Sniff the root element rather than trusting the file name. A results directory
            //  collects other people's xml over time, and a coverage report parsed as JUnit
            //  contributes nothing and says nothing about having contributed nothing. Loading it
            //  is what makes the sniff possible, so a file that will not load fails here by name
            //  rather than reaching the top as an XmlException about an anonymous line 1.
            if (Xml.Load(path, "a candidate junit file").Root?.Name.LocalName
                is not ("testsuites" or "testsuite"))
            {
                continue;
            }

            junitFiles.Add(path);
            webResults.AddRange(TestOutcomes.ReadJUnit(path));
        }

        return new Evidence(
            assemblies,
            runs,
            junitFiles,
            tags,
            runs.SelectMany(r => r.Results).ToList(),
            webResults);
    }

    /// <summary>Console-side tags: the requirement ids written into vitest test titles.</summary>
    internal IEnumerable<(string Id, TestResult Result)> WebTags()
    {
        foreach (TestResult result in WebResults)
        {
            foreach (Match match in WebTag.Matches(result.DisplayName))
            {
                yield return (match.Groups["id"].Value, result);
            }
        }
    }

    /// <summary>Results reported for a <c>[Verifies]</c> site, which may be none.</summary>
    internal IReadOnlyList<TestResult> ResultsFor(TagSite tag)
    {
        List<TestResult> matches = [];
        foreach (TestResult result in ManagedResults)
        {
            if (!result.ClassName.Equals(tag.TypeFullName, StringComparison.Ordinal))
            {
                continue;
            }

            //  A class-level tag claims every test in the class. A method-level one claims the
            //  method and, with it, every case of a theory -- hence the parenthesis, in case a
            //  future logger writes the arguments into the method name rather than beside it.
            if (tag.MethodName is null
                || result.MethodName.Equals(tag.MethodName, StringComparison.Ordinal)
                || result.MethodName.StartsWith($"{tag.MethodName}(", StringComparison.Ordinal))
            {
                matches.Add(result);
            }
        }

        return matches;
    }
}
