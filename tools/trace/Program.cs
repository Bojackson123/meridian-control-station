using System.Xml.Linq;
using Mcs.Trace;

//  The requirements trace.
//
//  docs/requirements.md claims, per row, how the requirement is verified and whether it is. This
//  checks those claims against what a test run actually reported and against whether the linked
//  evidence still exists, and fails the build when they disagree. A requirements table nobody
//  checks is a document; one a build step checks is a baseline, and the difference is this file.
//
//  Three rules, and one that makes them safe:
//
//    * a Test row needs at least one test that REPORTED PASSING and carries [Verifies("MCS-NNN")];
//    * an Inspection, Demonstration or Analysis row needs an `evidence:` link that resolves;
//    * a tag naming an id that is not in the table fails;
//    * and an explicit "not verified — <reason>" is a PASS, distinct from missing evidence.
//
//  That last one is not softness. Without it the cheapest way to a green build is to delete the
//  requirement, and a gate that rewards deleting requirements is worse than no gate. The ratchet
//  against deletion is the contiguity check below: an id may only go missing if the Removed table
//  names it.
//
//  Known limit, stated rather than hidden: the ratchet catches deletion but not downgrade. Nothing
//  here stops a row moving from "verified" to "not verified — <reason>" to make the build green.
//  Catching that needs the file's history, which this tool does not read, and the honest guard
//  remains that the downgrade is a visible line in a diff.

int exitCode = Run(args);
return exitCode;

static int Run(string[] args)
{
    Options options;
    try
    {
        options = Options.Parse(args);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Options.Usage);
        return 2;
    }
    catch (FormatException ex)
    {
        //  A file named correctly and unreadable. The usage text belongs above, where the
        //  arguments were wrong; printing it here would suggest the command line is the problem.
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    RequirementsTable.Document table;
    Evidence evidence;
    try
    {
        table = RequirementsTable.Read(options.RequirementsPath);
        evidence = Evidence.Gather(options.EvidenceDirectory);
    }
    catch (Exception ex) when (ex is FormatException or IOException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    Report report = new();
    Checks.InputsAreComplete(options, evidence, report);
    Checks.TableIsWellFormed(table, report);
    Checks.TagsNameRealRequirements(table, evidence, report);

    foreach (Requirement requirement in table.Requirements)
    {
        Checks.Requirement(requirement, options, evidence, report);
    }

    WriteInventory(options, table, evidence);
    return report.Write(Console.Out, Console.Error);
}

static void WriteInventory(
    Options options, RequirementsTable.Document table, Evidence evidence)
{
    int managedTags = evidence.Tags.Count;
    int webTags = evidence.WebTags().Count();

    Console.WriteLine();
    Console.WriteLine($"  requirements   {table.Requirements.Count} in {options.RequirementsPath}");
    Console.WriteLine(
        $"  assemblies     {evidence.Assemblies.Count} "
        + $"({string.Join(", ", evidence.Assemblies.Keys)})");
    Console.WriteLine(
        $"  result files   {evidence.TrxRuns.Count} trx, {evidence.JUnitFiles.Count} junit, "
        + $"{evidence.ManagedResults.Count + evidence.WebResults.Count} results");
    Console.WriteLine($"  tags           {managedTags} on tests, {webTags} in console titles");
}

/// <summary>Where to read the table, the evidence, and the list of suites that must have run.</summary>
internal sealed record Options(
    string RepositoryRoot,
    string RequirementsPath,
    string EvidenceDirectory,
    IReadOnlyList<string> ExpectedTestAssemblies)
{
    internal const string Usage = """
        usage: dotnet run --project tools/trace -- --evidence <dir> [--requirements <path>]
                                                   [--solution <path>]

          --evidence      directory holding the .trx files, the console's JUnit xml and the
                          built *.Tests.dll. Required.
          --requirements  defaults to docs/requirements.md beside the solution.
          --solution      defaults to the nearest *.slnx at or above the working directory; it
                          is read only for the list of test projects that must have reported.
        """;

    internal string DocsDirectory => Path.GetDirectoryName(RequirementsPath)!;

    internal static Options Parse(string[] args)
    {
        string? evidence = null;
        string? requirements = null;
        string? solution = null;

        for (int i = 0; i < args.Length; i++)
        {
            string Value(string name) => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{name} needs a value.");

            switch (args[i])
            {
                case "--evidence": evidence = Value("--evidence"); break;
                case "--requirements": requirements = Value("--requirements"); break;
                case "--solution": solution = Value("--solution"); break;
                default: throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
            }
        }

        if (evidence is null)
        {
            throw new ArgumentException("--evidence is required.");
        }

        solution ??= FindSolution(Directory.GetCurrentDirectory());
        string root = Path.GetDirectoryName(Path.GetFullPath(solution))!;

        return new Options(
            root,
            Path.GetFullPath(requirements ?? Path.Combine(root, "docs", "requirements.md")),
            Path.GetFullPath(evidence),
            ReadTestProjects(solution));
    }

    private static string FindSolution(string start)
    {
        for (DirectoryInfo? directory = new(start);
            directory is not null;
            directory = directory.Parent)
        {
            string[] found = Directory.GetFiles(directory.FullName, "*.slnx");
            if (found.Length == 1)
            {
                return found[0];
            }
        }

        throw new ArgumentException(
            $"No .slnx at or above '{start}'; pass --solution.");
    }

    //  The solution is the repository's own list of test projects, so it is what the completeness
    //  check is asserted against. Hardcoding the six suites here would mean a seventh suite could
    //  be added, never run, and never be missed.
    private static IReadOnlyList<string> ReadTestProjects(string solutionPath)
    {
        List<string> assemblies = [];
        foreach (XElement project in
            Xml.Load(solutionPath, "the solution's list of test projects").Descendants("Project"))
        {
            string path = ((string?)project.Attribute("Path") ?? string.Empty).Replace('\\', '/');
            if (!path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            //  Suites, not everything living under tests/. A shared support project -- the sort
            //  of thing tests/Verifies.cs would become if it ever needed a project of its own --
            //  would otherwise be demanded as evidence that nothing can supply: the tag reader
            //  collects *.Tests.dll and CI copies Mcs.*.Tests.dll, so the trace would go
            //  permanently red with an instruction ("build it and copy it in") that is already
            //  being followed.
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".Tests", StringComparison.Ordinal))
            {
                assemblies.Add(name);
            }
        }

        return assemblies;
    }
}
