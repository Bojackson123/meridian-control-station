using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcs.Api.Persistence;

/// <summary>
/// One numbered <c>.sql</c> file from <c>deploy/migrations</c>, with the checksum the ledger
/// records for it.
/// </summary>
/// <remarks>
/// The files are compiled into the assembly as embedded resources rather than copied next to it.
/// A migration is part of the build that ships it, so it should not be separately deletable,
/// mountable over, or absent from a container because a <c>COPY</c> line was forgotten -- the
/// failure that produces is an API that starts against a database it has not migrated.
/// </remarks>
internal sealed partial class MigrationScript
{
    /// <summary>
    /// <c>0042_add_something.sql</c>: the number orders the file and is its identity in the ledger.
    /// </summary>
    /// <remarks>
    /// Matched against the tail of the resource name rather than the whole of it. MSBuild derives
    /// resource names from the link path and mangles anything that is not a valid identifier, so
    /// the prefix is the build system's business and only the filename is ours.
    /// </remarks>
    [GeneratedRegex(@"(?<version>\d+)_(?<name>[A-Za-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    private MigrationScript(int version, string name, string sql, string checksum)
    {
        Version = version;
        Name = name;
        Sql = sql;
        Checksum = checksum;
    }

    /// <summary>Gets the number leading the filename; the primary key in <c>schema_version</c>.</summary>
    public int Version { get; }

    /// <summary>Gets the descriptive part of the filename, for the ledger and the logs.</summary>
    public string Name { get; }

    /// <summary>Gets the statements to execute, as authored.</summary>
    public string Sql { get; }

    /// <summary>
    /// Gets the lowercase hex SHA-256 of <see cref="Sql"/>, used to detect a file edited after it
    /// shipped.
    /// </summary>
    public string Checksum { get; }

    /// <summary>
    /// Loads every embedded migration, ordered by version.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No migrations are embedded, a filename is unparseable, a version is duplicated, or the
    /// versions do not run 1..N without a gap. All four mean the build is wrong, and all four are
    /// worth refusing to start over: the alternative is a station that silently runs against half a
    /// schema.
    /// </exception>
    public static IReadOnlyList<MigrationScript> LoadAll() => LoadAll(typeof(MigrationScript).Assembly);

    internal static IReadOnlyList<MigrationScript> LoadAll(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<MigrationScript> scripts = [];

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match match = FileNamePattern().Match(resourceName);

            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"Embedded migration '{resourceName}' is not named <number>_<name>.sql. The "
                    + "number is what orders it and what identifies it in schema_version, so a file "
                    + "without one cannot be applied safely.");
            }

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' could not be opened.");

            using StreamReader reader = new(stream, Encoding.UTF8);

            string sql = reader.ReadToEnd();

            scripts.Add(new MigrationScript(
                int.Parse(match.Groups["version"].ValueSpan, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                sql,
                ChecksumOf(sql)));
        }

        if (scripts.Count == 0)
        {
            throw new InvalidOperationException(
                "No migrations are embedded in the assembly. deploy/migrations/*.sql is included by "
                + "Mcs.Api.csproj; an empty set means that glob has stopped matching.");
        }

        scripts.Sort(static (left, right) => left.Version.CompareTo(right.Version));

        //  Contiguous from 1, so a file that never got committed -- or one that landed with a
        //  number someone else had already used -- is caught here rather than by whatever the gap
        //  turns out to have contained.
        for (int i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    $"Migration versions must run 1..{scripts.Count} without a gap or a duplicate; "
                    + $"found {scripts[i].Version} where {i + 1} was expected. Migrations present: "
                    + string.Join(", ", scripts.Select(static s => s.FileName)) + ".");
            }
        }

        return scripts;
    }

    /// <summary>Gets the filename this script was loaded from, for messages and the logs.</summary>
    public string FileName =>
        string.Create(CultureInfo.InvariantCulture, $"{Version:0000}_{Name}.sql");

    /// <inheritdoc />
    public override string ToString() => FileName;

    /// <summary>
    /// Hashes a script's text, ignoring carriage returns.
    /// </summary>
    /// <remarks>
    /// Line endings are normalised because <c>.gitattributes</c> checks these files out CRLF on
    /// Windows and LF in a Linux container, and the same migration hashing differently depending on
    /// where the build ran would turn the drift check into a coin toss. What is being detected is an
    /// edit to the statements, and a carriage return is not one.
    /// </remarks>
    private static string ChecksumOf(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r", string.Empty, StringComparison.Ordinal))));
}
