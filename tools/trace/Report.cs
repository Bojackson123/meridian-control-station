namespace Mcs.Trace;

/// <summary>
///   Collects what was wrong and prints it so that the fix is obvious from the log alone.
/// </summary>
/// <remarks>
///   Every line names the requirement, what was expected and what was found. "3 requirements
///   unverified" sends whoever is on the failure back to read the whole table; naming them is the
///   difference between a gate that gets fixed and one that gets disabled.
/// </remarks>
internal sealed class Report
{
    private readonly List<(string? Requirement, string Message)> _problems = [];

    internal void Add(string requirement, string message) => _problems.Add((requirement, message));

    internal void AddGlobal(string message) => _problems.Add((null, message));

    internal int Write(TextWriter output, TextWriter error)
    {
        if (_problems.Count == 0)
        {
            output.WriteLine();
            output.WriteLine("  every requirement is backed by the evidence it claims.");
            return 0;
        }

        error.WriteLine();
        error.WriteLine($"  {_problems.Count} problem(s):");
        error.WriteLine();

        foreach ((string? requirement, string message) in _problems
            .OrderBy(p => p.Requirement is null ? 0 : 1)
            .ThenBy(p => p.Requirement, StringComparer.Ordinal))
        {
            error.WriteLine(requirement is null ? $"  * {message}" : $"  * {requirement}: {message}");
        }

        error.WriteLine();
        return 1;
    }
}
