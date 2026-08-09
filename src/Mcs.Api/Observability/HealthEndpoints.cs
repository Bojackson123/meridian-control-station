using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Mcs.Api.Observability;

/// <summary>
/// The station's two health endpoints: <c>/health</c> for "this process is alive" and
/// <c>/health/db</c> for "this process can do its job".
/// </summary>
/// <remarks>
/// <b>The split is the point.</b> Compose and CI both need a liveness probe that stays green while
/// Postgres is still starting -- one endpoint that touches the database would have the container
/// runtime killing and restarting an API that was only waiting, which turns a slow start into a
/// crash loop. Readiness is the endpoint that is allowed to be red.
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>Liveness: the process is running and the pipeline responds.</summary>
    public const string LivenessPath = "/health";

    /// <summary>Readiness: everything the station needs to serve is reachable.</summary>
    public const string ReadinessPath = "/health/db";

    /// <summary>
    /// Maps both endpoints. Both answer JSON, so anything reading them -- the smoke suite, a probe,
    /// a person with <c>curl</c> -- parses one shape.
    /// </summary>
    public static IEndpointRouteBuilder MapStationHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks(LivenessPath, new HealthCheckOptions
        {
            //  Runs no checks at all: liveness must not be able to fail for a reason outside this
            //  process, or it stops meaning "alive".
            Predicate = static _ => false,
            ResponseWriter = WriteHealthResponseAsync,
        });

        endpoints.MapHealthChecks(ReadinessPath, new HealthCheckOptions
        {
            Predicate = static registration =>
                registration.Tags.Contains(Persistence.SchemaVersionHealthCheck.ReadinessTag),
            ResponseWriter = WriteHealthResponseAsync,
        });

        return endpoints;
    }

    /// <summary>
    /// Writes the report as a flat JSON object: the overall status, plus whatever data the checks
    /// chose to publish.
    /// </summary>
    /// <remarks>
    /// Flat rather than nested per check, because there is one check and a nested shape would make
    /// the smoke suite's assertion three levels deep for no information. If a second readiness check
    /// ever lands on this path, that is the moment to nest it -- and the collision between two
    /// checks publishing the same key is what will say so.
    /// <para>
    /// Only <see cref="HealthReportEntry.Data"/> is written. Descriptions and exceptions stay in the
    /// log: this endpoint is reachable by anything that can reach the port, and a failing database
    /// puts host names and credentials in its exception message.
    /// </para>
    /// </remarks>
    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        Dictionary<string, object> payload = new(StringComparer.Ordinal)
        {
            ["status"] = report.Status.ToString(),
        };

        foreach (KeyValuePair<string, HealthReportEntry> entry in report.Entries)
        {
            foreach (KeyValuePair<string, object> item in entry.Value.Data)
            {
                payload[item.Key] = item.Value;
            }
        }

        return context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
    }
}
