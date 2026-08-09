using System.Reflection;

using Mcs.Api.Persistence;

namespace Mcs.Api.Tests;

/// <summary>
/// The claims the migration loader makes before a database is anywhere in sight: the files are in
/// the build, they are numbered without a gap, and the checksum that decides whether a schema has
/// drifted is a property of the statements rather than of the machine that built them.
/// </summary>
/// <remarks>
/// These need no container, which is the point of separating them. A build that has stopped
/// embedding its migrations fails here in a second rather than in the integration suite behind a
/// Docker daemon, and the message says which of the two things went wrong.
/// </remarks>
public class MigrationScriptTests
{
    private static Assembly ApiAssembly => typeof(SchemaMigrator).Assembly;

    [Fact]
    public void LoadAll_FindsTheMigrationsEmbeddedInTheApiAssembly()
    {
        // The whole mechanism rests on the .sql glob in Mcs.Api.csproj continuing to match. Nothing
        // else in the system notices if it stops -- the API would start, apply no migrations, and
        // report a healthy connection to an empty database.
        IReadOnlyList<MigrationScript> scripts = MigrationScript.LoadAll(ApiAssembly);

        Assert.NotEmpty(scripts);
        Assert.Contains(scripts, static script => script.FileName == "0001_baseline.sql");
    }

    [Fact]
    public void LoadAll_ReturnsScriptsNumberedFromOneWithoutAGap()
    {
        IReadOnlyList<MigrationScript> scripts = MigrationScript.LoadAll(ApiAssembly);

        // Order is the contract: 0002 assumes 0001 has run. A gap means a file that was written and
        // never committed, and applying what remains would leave a schema nobody has ever tested.
        Assert.Equal(
            Enumerable.Range(1, scripts.Count),
            scripts.Select(static script => script.Version));
    }

    [Fact]
    public void Baseline_CreatesTheLedgerTable()
    {
        // 0001 is the one migration the runner cannot bootstrap around: it reads schema_version to
        // decide what to apply, and this is the file that brings it into existence.
        MigrationScript baseline = MigrationScript.LoadAll(ApiAssembly)[0];

        Assert.Contains("schema_version", baseline.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS", baseline.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Checksum_IsTheSameWhicheverLineEndingTheFileWasCheckedOutWith()
    {
        // The reason the hash ignores carriage returns. These files are checked out CRLF on Windows
        // and LF in the Linux image, so a byte-for-byte hash would make the drift check depend on
        // where the build ran -- and the first symptom would be a container refusing to start
        // against the database a developer had just migrated from their laptop.
        MigrationScript[] windows = [.. Load("CREATE TABLE t (a int);\r\nSELECT 1;\r\n")];
        MigrationScript[] linux = [.. Load("CREATE TABLE t (a int);\nSELECT 1;\n")];

        Assert.Equal(linux[0].Checksum, windows[0].Checksum);
    }

    [Fact]
    public void Checksum_ChangesWhenTheStatementsChange()
    {
        // The other half: normalising newlines must not have blunted the thing into uselessness.
        MigrationScript[] original = [.. Load("CREATE TABLE t (a int);\n")];
        MigrationScript[] edited = [.. Load("CREATE TABLE t (a bigint);\n")];

        Assert.NotEqual(original[0].Checksum, edited[0].Checksum);
    }

    [Fact]
    public void Checksum_IsALowercaseHexSha256()
    {
        // Pinned because it is written into a database and compared against on every start: changing
        // the format silently would read as drift on every migration ever applied.
        MigrationScript script = MigrationScript.LoadAll(ApiAssembly)[0];

        Assert.Equal(64, script.Checksum.Length);
        Assert.All(script.Checksum, static c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void LoadAll_AssemblyWithNoMigrations_SaysSoRatherThanReturningNothing()
    {
        // An empty result would be indistinguishable from "everything is already applied", so the
        // API would start, migrate nothing, and wait for the first query to fail.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => MigrationScript.LoadAll(typeof(object).Assembly));

        Assert.Contains("No migrations are embedded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAll_MigrationsThatSkipANumber_AreRejected()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Load(("0001_first.sql", "SELECT 1;"), ("0003_third.sql", "SELECT 3;")));

        Assert.Contains("without a gap", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0003_third.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAll_MigrationsSharingANumber_AreRejected()
    {
        // Two files claiming version 1 means one of them is recorded and the other silently never
        // runs, with which one depending on enumeration order.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Load(("0001_first.sql", "SELECT 1;"), ("0001_also_first.sql", "SELECT 2;")));

        Assert.Contains("duplicate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAll_AFileWithNoLeadingNumber_IsRejected()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Load(("baseline.sql", "SELECT 1;")));

        Assert.Contains("baseline.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAll_OrdersByNumberRatherThanByName()
    {
        // Resource enumeration order is not specified, and the number is parsed rather than
        // compared as text: "10" sorts before "2" as a string, so a set that reaches double figures
        // without zero-padding would apply the tenth migration second. Zero-padding is the
        // convention and it hides this -- which is exactly why the sort must not depend on it.
        IReadOnlyList<MigrationScript> scripts = Load(
            [.. Enumerable.Range(1, 10)
                .Reverse()
                .Select(static i => ($"{i}_step.sql", $"SELECT {i};"))]);

        Assert.Equal(Enumerable.Range(1, 10), scripts.Select(static script => script.Version));
    }

    /// <summary>Loads a single unnamed script, for the checksum cases.</summary>
    private static IReadOnlyList<MigrationScript> Load(string sql) => Load(("0001_baseline.sql", sql));

    /// <summary>
    /// Loads migrations from a stand-in assembly whose embedded resources are the given files.
    /// </summary>
    /// <remarks>
    /// The gap, duplicate and ordering rules only bite on migration sets that must never reach the
    /// repository, so they cannot be exercised through the real assembly. A fake
    /// <see cref="Assembly"/> is the smallest way to reach them, and it keeps the loader's contract
    /// stated in tests rather than in a comment hoping to be read.
    /// <para>
    /// Version numbers here deliberately skip the contiguity rule where a case needs them to.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<MigrationScript> Load(params (string FileName, string Sql)[] files) =>
        MigrationScript.LoadAll(new FakeMigrationAssembly(files));
}
