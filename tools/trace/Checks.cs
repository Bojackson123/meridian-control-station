namespace Mcs.Trace;

/// <summary>The checks themselves, one method each.</summary>
internal static class Checks
{
    /// <summary>
    ///   Every test project in the solution must have contributed both an assembly and a result
    ///   file, and the console must have contributed one.
    /// </summary>
    /// <remarks>
    ///   Without this, a suite absent from the evidence directory takes its tags with it and the
    ///   requirements it covered fail with "no passing test tagged" -- a true statement that sends
    ///   whoever reads it to the wrong place. Worse, the unknown-id check can only see the tags in
    ///   front of it, so a whole suite of misspelled ids would pass unremarked. Assert the inputs,
    ///   then trust them.
    /// </remarks>
    internal static void InputsAreComplete(Options options, Evidence evidence, Report report)
    {
        HashSet<string> ranSomewhere = new(StringComparer.OrdinalIgnoreCase);
        foreach (TestOutcomes.TrxRun run in evidence.TrxRuns)
        {
            ranSomewhere.UnionWith(run.Assemblies);
        }

        foreach (string assembly in options.ExpectedTestAssemblies)
        {
            if (!evidence.Assemblies.ContainsKey(assembly))
            {
                report.AddGlobal(
                    $"{assembly}.dll is not in the evidence directory, so any [Verifies] tag in "
                    + "it is invisible here. Build it and copy it in.");
            }

            if (!ranSomewhere.Contains(assembly))
            {
                report.AddGlobal(
                    $"no .trx reports a run of {assembly}. A suite that did not run cannot be "
                    + "evidence for anything; run it with --logger trx.");
            }
        }

        if (evidence.JUnitFiles.Count == 0)
        {
            report.AddGlobal(
                "no JUnit xml in the evidence directory, so the console's tests reported nothing. "
                + "Run vitest with --reporter=junit.");
        }
    }

    /// <summary>
    ///   Ids run contiguously from 001, and one may only go missing by being named in the Removed
    ///   table.
    /// </summary>
    /// <remarks>
    ///   This is the anti-deletion ratchet, and it is the reason the rest of the tool can afford
    ///   to be strict: the cheapest route to a green table is otherwise to delete the row that is
    ///   red, which loses the requirement and leaves no trace that there ever was one. Removing
    ///   one now costs a line saying which and why.
    /// </remarks>
    internal static void TableIsWellFormed(RequirementsTable.Document table, Report report)
    {
        HashSet<int> present = [.. table.Requirements.Select(r => r.Number)];
        int highest = present.Max();

        for (int number = 1; number <= highest; number++)
        {
            if (present.Contains(number))
            {
                continue;
            }

            string id = $"MCS-{number:000}";
            if (!table.RemovedIds.Contains(id))
            {
                report.AddGlobal(
                    $"{id} is missing from the table and from the Removed section. A requirement "
                    + "leaves the table by getting a line there saying why, not by being deleted.");
            }
        }
    }

    /// <summary>A tag naming an id the table does not have fails, wherever the tag lives.</summary>
    /// <remarks>
    ///   The check that catches the renumbering and the typo, and the one that keeps working after
    ///   everyone has stopped thinking about it. It covers the console's title tags too, or a
    ///   stray [MCS-014] in a vitest title would sit there verifying nothing.
    /// </remarks>
    internal static void TagsNameRealRequirements(
        RequirementsTable.Document table, Evidence evidence, Report report)
    {
        HashSet<string> known = [.. table.Requirements.Select(r => r.Id)];

        foreach (TagSite tag in evidence.Tags)
        {
            if (!known.Contains(tag.RequirementId))
            {
                report.AddGlobal(
                    $"{tag} is tagged [Verifies(\"{tag.RequirementId}\")], and there is no such "
                    + "row in the table.");
            }
        }

        foreach ((string id, TestResult result) in evidence.WebTags())
        {
            if (!known.Contains(id))
            {
                report.AddGlobal(
                    $"the console test '{result.DisplayName}' claims {id}, and there is no such "
                    + "row in the table.");
            }
        }
    }

    /// <summary>The per-row rules: what each status owes, and what each method owes.</summary>
    internal static void Requirement(
        Requirement requirement, Options options, Evidence evidence, Report report)
    {
        if (requirement.Status is not VerificationStatus.Verified
            && string.IsNullOrWhiteSpace(requirement.StatusReason))
        {
            report.Add(
                requirement.Id,
                "the status says it is not fully verified without saying why. A row that is not "
                + "verified owes a reason and what would change it.");
        }

        if (requirement.Status is VerificationStatus.NotVerified)
        {
            //  The row is exempt from its own method checks -- that is what makes an honest
            //  "not built yet" a pass. The one thing asserted is the opposite direction: a row
            //  claiming no evidence while tests quietly supply it is stale too, and stale in the
            //  direction that makes the project look worse than it is.
            IReadOnlyList<TestResult> passing = [.. PassingFor(requirement.Id, evidence)];
            if (passing.Count > 0)
            {
                report.Add(
                    requirement.Id,
                    $"is marked not verified, but {passing.Count} tagged test(s) pass against it "
                    + $"— e.g. {passing[0].DisplayName}. Either the row or the tag is out of date.");
            }

            return;
        }

        foreach (VerificationMethod method in requirement.Methods)
        {
            if (method is VerificationMethod.Test)
            {
                TestEvidence(requirement, evidence, report);
            }
            else
            {
                LinkIsPresent(requirement, method, report);
            }
        }

        //  Every link in the section is resolved, not only the links on rows that owe one. Which
        //  methods a row claims decides whether a link is *required*; it says nothing about
        //  whether a link that is there still points at something. A Test row citing a note is
        //  ordinary -- MCS-001's measured latency budget is exactly that -- and a link this tool
        //  parsed and then declined to check is a dead link the README says CI is checking.
        foreach (EvidenceLink link in requirement.Evidence)
        {
            if (Resolve(link.Target, options) is { } why)
            {
                report.Add(requirement.Id, $"line {link.Line}: {why}");
            }
        }
    }

    private static void TestEvidence(Requirement requirement, Evidence evidence, Report report)
    {
        List<TestResult> tagged = [];
        int sites = 0;

        foreach (TagSite tag in evidence.Tags.Where(t => t.RequirementId == requirement.Id))
        {
            sites++;
            IReadOnlyList<TestResult> results = evidence.ResultsFor(tag);
            if (results.Count == 0)
            {
                //  Tagged, and no run has anything to say about it. Usually a suite dropped out
                //  of the run; occasionally a tag on something that is not a test at all.
                report.Add(
                    requirement.Id,
                    $"{tag} carries the tag, and no test run reported it at all.");
                continue;
            }

            tagged.AddRange(results);
        }

        foreach ((string _, TestResult result) in evidence.WebTags()
            .Where(t => t.Id == requirement.Id))
        {
            sites++;
            tagged.Add(result);
        }

        if (sites == 0)
        {
            report.Add(
                requirement.Id,
                "claims Method: Test, and no test carries [Verifies(\"" + requirement.Id
                + "\")] — or, in the console, that id in its title.");
            return;
        }

        if (tagged.Count == 0)
        {
            //  Every site was reported missing above. Saying "no test carries the tag" here as
            //  well would be false and would send the reader to write a test that already exists.
            return;
        }

        //  A skipped test is the whole reason this reads results rather than source. It satisfies
        //  every check that asks "is there a test for this" and proves nothing whatever, and it is
        //  indistinguishable from a passing one to anyone reading the file it lives in.
        foreach (TestResult skipped in tagged.Where(t => t.Outcome is TestOutcome.Skipped))
        {
            report.Add(
                requirement.Id,
                $"'{skipped.DisplayName}' is tagged for it and was skipped, so it is evidence of "
                + "nothing. A skipped test is not a passing test.");
        }

        foreach (TestResult failed in tagged.Where(t => t.Outcome is TestOutcome.Failed))
        {
            report.Add(requirement.Id, $"'{failed.DisplayName}' is tagged for it and failed.");
        }

        if (!tagged.Any(t => t.Outcome is TestOutcome.Passed))
        {
            report.Add(
                requirement.Id,
                $"has {tagged.Count} tagged test(s) and not one of them passed.");
        }
    }

    /// <summary>A method that is a human looking at an artifact owes a link to the artifact.</summary>
    private static void LinkIsPresent(
        Requirement requirement, VerificationMethod method, Report report)
    {
        if (requirement.Evidence.Count == 0)
        {
            report.Add(
                requirement.Id,
                $"claims Method: {method}, which is a human looking at an artifact, and its "
                + "section links no artifact. A row claiming Analysis or Inspection with no "
                + "`evidence:` link is a row claiming nothing.");
        }
    }

    /// <summary>Null when the link resolves; otherwise why it does not.</summary>
    /// <remarks>
    ///   Worth more than it looks over a year. Evidence points at notes and workflow files, notes
    ///   get renamed, and this is the only mechanism in the repository that notices.
    /// </remarks>
    private static string? Resolve(string target, Options options)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https")
        {
            //  A URL is not fetched -- a build step that reaches the network is a build step that
            //  goes red when someone else's server does. What can be checked offline is a link
            //  into this repository, whose path is right here.
            if (!uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string[] segments = uri.AbsolutePath.Trim('/').Split('/');
            int blob = Array.FindIndex(segments, s => s is "blob" or "tree");
            if (blob < 0 || blob + 2 >= segments.Length)
            {
                return null;
            }

            string inRepo = string.Join('/', segments.Skip(blob + 2));
            return File.Exists(Path.Combine(options.RepositoryRoot, inRepo))
                || Directory.Exists(Path.Combine(options.RepositoryRoot, inRepo))
                    ? null
                    : $"`evidence: {target}` points into this repository at '{inRepo}', "
                        + "which does not exist.";
        }

        if (uri is not null && uri.IsAbsoluteUri)
        {
            return $"`evidence: {target}` is a {uri.Scheme} url; evidence is a path in the "
                + "repository or an http(s) link.";
        }

        //  Relative to docs/, because that is where requirements.md sits and what a reader
        //  clicking the link in a rendered view would get.
        string resolved = Path.GetFullPath(Path.Combine(options.DocsDirectory, target));

        //  The separator is part of the comparison. Without it a sibling checkout whose name
        //  merely begins with this one's -- ../meridian-control-station-notes/x.md, which is
        //  exactly the shape of directory someone keeps beside a repository -- is inside the
        //  repository as far as a prefix test is concerned, and is accepted as evidence on the one
        //  machine where it exists. That is the case this check was written for.
        string root = Path.TrimEndingDirectorySeparator(options.RepositoryRoot);
        if (!resolved.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !resolved.StartsWith(
                root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return $"`evidence: {target}` resolves to '{resolved}', outside the repository. "
                + "Evidence that lives on one machine is not evidence.";
        }

        return File.Exists(resolved) || Directory.Exists(resolved)
            ? null
            : $"`evidence: {target}` does not resolve — nothing at '{resolved}'.";
    }

    private static IEnumerable<TestResult> PassingFor(string id, Evidence evidence)
    {
        foreach (TagSite tag in evidence.Tags.Where(t => t.RequirementId == id))
        {
            foreach (TestResult result in evidence.ResultsFor(tag))
            {
                if (result.Outcome is TestOutcome.Passed)
                {
                    yield return result;
                }
            }
        }

        foreach ((string tagged, TestResult result) in evidence.WebTags())
        {
            if (tagged == id && result.Outcome is TestOutcome.Passed)
            {
                yield return result;
            }
        }
    }
}
