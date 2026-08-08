using System.Globalization;

namespace Mcs.Core.Tests;

/// <summary>
/// Sets <see cref="CultureInfo.CurrentCulture"/> for the duration of a test and restores it after.
/// </summary>
/// <remarks>
/// Ambient culture is the one piece of global state these tests touch, so it is confined to the
/// few tests that genuinely exercise a culture-dependent path: <c>Altitude.ToString()</c>'s
/// invariance, the <see cref="IFormattable"/> overload's <c>CurrentCulture</c> fallback, and
/// <c>VehicleId</c>'s ordinal comparison. Everywhere else the tests pass an explicit
/// <see cref="IFormatProvider"/> and leave ambient state alone.
/// <para>
/// Culture is thread-scoped, and an async continuation can resume on a different thread than the
/// one that set it -- which would restore the culture on the wrong thread and leak it into
/// whatever ran next. Every test using this must therefore be synchronous, and must live in
/// <see cref="CultureCollection"/> so no other test class runs beside it.
/// </para>
/// </remarks>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _original;

    public CultureScope(string name)
    {
        _original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
    }

    public void Dispose() => CultureInfo.CurrentCulture = _original;
}

/// <summary>
/// Collection for test classes that mutate <see cref="CultureInfo.CurrentCulture"/> via
/// <see cref="CultureScope"/>. Parallelism is disabled so the mutation cannot be observed by a
/// test running concurrently in another class.
/// </summary>
[CollectionDefinition(CultureCollection.Name, DisableParallelization = true)]
public sealed class CultureCollection
{
    public const string Name = "Culture (mutates CultureInfo.CurrentCulture)";
}
