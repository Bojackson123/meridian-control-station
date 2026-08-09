using System.ComponentModel.DataAnnotations;

using Mcs.Core;

namespace Mcs.Api.FakeFeed;

/// <summary>
/// The <c>FakeFeed</c> configuration section: how many vehicles the feed flies, how fast it reports,
/// and the circuit they fly.
/// </summary>
/// <remarks>
/// Bounds are attributes and are checked at startup, so a typo fails the host with the offending
/// setting named rather than flying a plausible-looking circuit somewhere nobody meant.
/// <para>
/// Mutable properties because that is what the configuration binder needs; the validated, immutable
/// form is <see cref="CircularCourse"/>. The altitude <i>reference</i> is deliberately not
/// configurable -- offering it as a string invites a deployment where the number means something
/// other than what the console was written against.
/// </para>
/// </remarks>
public sealed class FakeFeedOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "FakeFeed";

    /// <summary>
    /// Gets or sets how many vehicles fly the circuit, evenly spaced around it.
    /// </summary>
    /// <remarks>
    /// Capped at <see cref="ITelemetryStore.MaxVehicles"/>, so configuration cannot ask the feed to
    /// do the one thing the store is documented to refuse.
    /// </remarks>
    [Range(1, ITelemetryStore.MaxVehicles)]
    public int VehicleCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets how many frames per second each vehicle reports.
    /// </summary>
    /// <remarks>
    /// The ceiling is the rate <see cref="ITelemetryStore.HistoryDepthPerVehicle"/> is sized against;
    /// above it the store's "one minute of history" stops being true.
    /// </remarks>
    [Range(0.1, 10.0)]
    public double RateHz { get; set; } = 1.0;

    /// <summary>Gets or sets the latitude of the circuit's centre. Defaults to Huntsville, Alabama.</summary>
    [Range(-85.0, 85.0)]
    public double OriginLatitudeDegrees { get; set; } = 34.7304;

    /// <summary>Gets or sets the longitude of the circuit's centre.</summary>
    [Range(-180.0, 180.0)]
    public double OriginLongitudeDegrees { get; set; } = -86.5861;

    /// <summary>
    /// Gets or sets the circuit radius in metres. The default fits one screen of map at a zoom where
    /// the ground underneath is still recognisable.
    /// </summary>
    [Range(50.0, 50_000.0)]
    public double RadiusMeters { get; set; } = 400.0;

    /// <summary>
    /// Gets or sets how many seconds one lap takes. With the default radius this is 20.9 m/s, a
    /// believable cruise for a small fixed-wing UAV.
    /// </summary>
    [Range(10.0, 3_600.0)]
    public double OrbitPeriodSeconds { get; set; } = 120.0;

    /// <summary>
    /// Gets or sets the altitude flown, in metres above mean sea level. Huntsville's terrain is
    /// around 190 m MSL, so the default is roughly 110 m above the ground.
    /// </summary>
    [Range(-500.0, 20_000.0)]
    public double AltitudeMetersMsl { get; set; } = 300.0;

    /// <summary>
    /// Gets or sets how many seconds the battery takes to drain from full to flat. The default is
    /// long enough that a demo never shows a flat battery, short enough that the number visibly moves
    /// within a minute of watching.
    /// </summary>
    [Range(60.0, 86_400.0)]
    public double EnduranceSeconds { get; set; } = 2_700.0;
}
