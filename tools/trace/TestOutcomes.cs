using System.Xml.Linq;

namespace Mcs.Trace;

/// <summary>What a test run said about a test.</summary>
/// <remarks>
///   <see cref="Skipped"/> is kept apart from <see cref="Failed"/> because the message an operator
///   of this tool needs is different: a failing test is a broken system, a skipped one is a
///   requirement whose evidence quietly stopped running. The second is the reason this tool reads
///   results at all -- it is the failure <c>MCS_SMOKE_REQUIRED</c> exists to prevent, one layer up.
/// </remarks>
internal enum TestOutcome
{
    Passed,
    Skipped,
    Failed,
}

/// <summary>One test as a run reported it.</summary>
internal sealed record TestResult(
    string ClassName,
    string MethodName,
    string DisplayName,
    TestOutcome Outcome);

/// <summary>
///   Reads TRX and JUnit result files. Nothing here looks at test source: a tag proves nothing
///   until a run says the test it sits on passed.
/// </summary>
internal static class TestOutcomes
{
    private static readonly XNamespace Trx =
        "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>A TRX file: the assemblies it ran, and what it said about each test.</summary>
    internal sealed record TrxRun(
        string Path,
        IReadOnlySet<string> Assemblies,
        IReadOnlyList<TestResult> Results);

    internal static TrxRun ReadTrx(string path)
    {
        XDocument document = Xml.Load(path, "a test run's trx");

        //  className and name off <TestMethod>, not the display name off <UnitTest>. The display
        //  name carries a theory's arguments and, in some hosting modes, a uniqueness suffix; the
        //  two attributes below are the split of FullyQualifiedName and are stable under both.
        Dictionary<string, (string ClassName, string MethodName, string DisplayName)> definitions =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> assemblies = new(StringComparer.OrdinalIgnoreCase);

        foreach (XElement test in document.Descendants(Trx + "UnitTest"))
        {
            string? id = (string?)test.Attribute("id");
            XElement? method = test.Element(Trx + "TestMethod");
            if (id is null || method is null)
            {
                continue;
            }

            definitions[id] = (
                (string?)method.Attribute("className") ?? string.Empty,
                (string?)method.Attribute("name") ?? string.Empty,
                (string?)test.Attribute("name") ?? string.Empty);

            if ((string?)test.Attribute("storage") is { Length: > 0 } storage)
            {
                assemblies.Add(System.IO.Path.GetFileNameWithoutExtension(storage));
            }
        }

        List<TestResult> results = [];
        foreach (XElement result in document.Descendants(Trx + "UnitTestResult"))
        {
            string? testId = (string?)result.Attribute("testId");
            if (testId is null || !definitions.TryGetValue(testId, out var definition))
            {
                continue;
            }

            results.Add(new TestResult(
                definition.ClassName,
                definition.MethodName,
                definition.DisplayName,
                Map((string?)result.Attribute("outcome"))));
        }

        return new TrxRun(path, assemblies, results);
    }

    /// <summary>
    ///   A JUnit file, as vitest writes it. <c>classname</c> is the test file's path and never the
    ///   suite chain, so <c>name</c> -- which is the flattened <c>describe &gt; … &gt; it</c> --
    ///   is the only place a console-side tag can live.
    /// </summary>
    internal static IReadOnlyList<TestResult> ReadJUnit(string path)
    {
        XDocument document = Xml.Load(path, "the console's junit results");

        List<TestResult> results = [];
        foreach (XElement test in document.Descendants("testcase"))
        {
            string name = (string?)test.Attribute("name") ?? string.Empty;
            string className = (string?)test.Attribute("classname") ?? string.Empty;

            //  A pass carries no child element at all; a skip and a failure each announce
            //  themselves with one. Reading the enclosing <testsuite> counters instead would tell
            //  us how many were skipped and not which.
            TestOutcome outcome =
                test.Element("skipped") is not null ? TestOutcome.Skipped
                : test.Elements().Any(e => e.Name.LocalName is "failure" or "error")
                    ? TestOutcome.Failed
                    : TestOutcome.Passed;

            results.Add(new TestResult(className, MethodName: string.Empty, name, outcome));
        }

        return results;
    }

    //  NotExecuted is what the TRX schema calls a skip, and it is what an xUnit Skip= arrives as.
    //  Everything that is neither passed nor skipped is a failure here; this tool does not need to
    //  tell a timeout from an assertion, only to refuse to call it evidence.
    private static TestOutcome Map(string? outcome) => outcome switch
    {
        "Passed" => TestOutcome.Passed,
        "NotExecuted" => TestOutcome.Skipped,
        _ => TestOutcome.Failed,
    };
}
