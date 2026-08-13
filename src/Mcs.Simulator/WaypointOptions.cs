using System.ComponentModel.DataAnnotations;

using Mcs.Simulator.Flight;

namespace Mcs.Simulator;

/// <summary>One waypoint as configuration binds it, before it becomes a <see cref="Waypoint"/>.</summary>
/// <remarks>
/// Mutable properties with a parameterless constructor because that is what the configuration
/// binder needs; the validated, immutable form is <see cref="Waypoint"/>. In the environment a
/// route element is <c>Simulator__Route__0__LatitudeDegrees</c>, which is ugly enough that the
/// route belongs in <c>appsettings.json</c> and the environment is for overriding one number.
/// <para>
/// The altitude reference is deliberately not configurable. Offering it as a string invites a
/// deployment where the number means something other than what the rest of this process was
/// written against, which is the confusion the station's altitude handling exists to prevent.
/// </para>
/// </remarks>
public sealed class WaypointOptions
{
    /// <summary>Gets or sets the WGS-84 latitude.</summary>
    [Range(-85.0, 85.0)]
    public double LatitudeDegrees { get; set; }

    /// <summary>Gets or sets the WGS-84 longitude.</summary>
    [Range(-180.0, 180.0)]
    public double LongitudeDegrees { get; set; }

    /// <summary>Gets or sets the altitude to hold at this point, in metres above mean sea level.</summary>
    [Range(-500.0, 20_000.0)]
    public double AltitudeMetersMsl { get; set; }
}
