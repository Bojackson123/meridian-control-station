using System.ComponentModel.DataAnnotations;

namespace Mcs.Api.Persistence;

/// <summary>
/// The <c>Database</c> configuration section: how long the station will wait for Postgres to accept
/// connections before it gives up and stops.
/// </summary>
/// <remarks>
/// The connection string is deliberately not here. It lives at <c>ConnectionStrings:Mcs</c>, where
/// ASP.NET's <c>ConnectionStrings__Mcs</c> environment-variable convention picks it up with no code,
/// and it is never committed -- <c>appsettings.json</c> has no value for it at all, so a deployment
/// that forgets to supply one fails by name instead of quietly connecting to whatever is listening
/// on the deployment host's localhost.
/// </remarks>
public sealed class DatabaseOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Database";

    /// <summary>The name of the connection string the station uses.</summary>
    public const string ConnectionStringName = "Mcs";

    /// <summary>The environment variable that supplies it, quoted in the failure message.</summary>
    public const string ConnectionStringEnvironmentVariable = "ConnectionStrings__Mcs";

    /// <summary>
    /// Gets or sets how many seconds the station will keep retrying an unreachable Postgres at
    /// startup before failing.
    /// </summary>
    /// <remarks>
    /// The API reliably comes up before Postgres accepts connections -- sometimes under Compose,
    /// often on a CI runner, which is slower than a laptop and is where this becomes a flaky build
    /// rather than a slow one. Compose's own healthcheck and <c>depends_on</c> cover the same
    /// ground; both belong here, because the container orchestrator is not the only way this process
    /// gets started.
    /// <para>
    /// Thirty seconds is a first-start Postgres initialising its data directory, with room to spare,
    /// and short enough that a genuinely misconfigured host is told so while someone is still
    /// watching.
    /// </para>
    /// </remarks>
    [Range(1, 300)]
    public int StartupTimeoutSeconds { get; set; } = 30;
}
