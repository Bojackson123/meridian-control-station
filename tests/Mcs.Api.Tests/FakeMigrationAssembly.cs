using System.Reflection;
using System.Text;

namespace Mcs.Api.Tests;

/// <summary>
/// An <see cref="Assembly"/> that carries nothing but the migration files a test hands it.
/// </summary>
/// <remarks>
/// The loader's rules about gaps, duplicates and unparseable names can only be reached with a set of
/// migrations that must never exist in the repository. Building one on disk and rebuilding the API
/// around it would be a slow way to assert something the loader decides from two method calls, so
/// the two calls are what gets faked.
/// <para>
/// <see cref="Assembly"/>'s members are virtual and throw by default, so anything the loader might
/// start using that is not overridden here fails loudly rather than returning a plausible empty
/// answer.
/// </para>
/// </remarks>
internal sealed class FakeMigrationAssembly : Assembly
{
    private readonly Dictionary<string, string> _files;

    public FakeMigrationAssembly(IEnumerable<(string FileName, string Sql)> files) =>
        _files = files.ToDictionary(
            static file => file.FileName,
            static file => file.Sql,
            StringComparer.Ordinal);

    /// <inheritdoc />
    public override string[] GetManifestResourceNames() => [.. _files.Keys];

    /// <inheritdoc />
    public override Stream? GetManifestResourceStream(string name) =>
        _files.TryGetValue(name, out string? sql)
            ? new MemoryStream(Encoding.UTF8.GetBytes(sql))
            : null;
}
