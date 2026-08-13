namespace Mcs.Simulator.Flight;

/// <summary>
/// Flies a looping route: points the aircraft at the active waypoint, and moves to the next one
/// when it comes within the capture radius.
/// </summary>
/// <remarks>
/// <b>Capture radius, not cross-track.</b> The alternative -- steer to null the perpendicular
/// distance from the leg, and the along-track distance decides when the leg is done -- tracks a
/// straight line far more tidily, and was rejected for what it does to the turn. Cross-track needs
/// a gain, so how hard the aircraft turns becomes a property of a tuning constant rather than of
/// the bank limit; a deconfliction margin derived from <c>v²/(g·tan φ)</c> would then be
/// describing an aircraft that turns for a different reason, and the document stating the bound
/// would have to describe the gain as well. Here the only thing that decides a turn is the
/// envelope, so the bound describes the whole of it. The price is that the aircraft cuts corners
/// and drifts downwind of a leg it is blown off -- neither of which matters, because there is no
/// wind and nothing is measuring track-keeping.
///
/// <para>
/// <b>The capture radius may not be smaller than the turn radius.</b> If it is, an aircraft that
/// overshoots can circle a waypoint it is never able to reach: the closest it can come is the
/// distance from its turn circle to the point, and the tightest circle it can fly has radius
/// <c>R</c>. That is a livelock, and it renders as an aircraft holding a neat orbit -- a picture
/// that looks like a deliberate loiter and is a misconfiguration. Rejected at construction rather
/// than clamped, because a clamped capture radius would fly a route nobody configured.
/// </para>
///
/// <para>
/// <b>At most one waypoint per call, and a route whose legs the capture radius swallows is
/// refused.</b> Capturing once per call bounds the damage to one waypoint per step; it does not
/// prevent it, and a capture radius at least half the shortest leg captures the next waypoint on
/// the very next step, for ever. The index then cycles at the step rate and the aircraft flies
/// nothing resembling the configured route -- which is not a rounder version of the shape, it is a
/// different picture entirely, so it is rejected at construction rather than warned about like the
/// tight-leg case. The threshold is <see cref="MinimumLegCaptureRadii"/> radii per leg, which is
/// where "arrived at this waypoint" and "arrived at the next one" stop being distinguishable.
/// </para>
///
/// <para>
/// <b>Nothing here logs</b>, matching the codec: this type is called at the simulation rate, and
/// a line per waypoint capture is fine until someone sets the step to 100 Hz. What it exposes
/// instead is state the host reads when it wants to say something.
/// </para>
///
/// <para><b>Not thread-safe.</b> One follower per aircraft, driven by the one loop flying it.</para>
/// </remarks>
internal sealed class WaypointFollower
{
    /// <summary>
    /// Below this a leg has no usable bearing: the displacement is zero to floating-point
    /// precision, and <c>Atan2(0, 0)</c> is due north rather than an error.
    /// </summary>
    private const double MinimumLegMeters = 1.0;

    /// <summary>
    /// How many turn radii a leg wants to be before the turns at its ends stop overlapping.
    /// </summary>
    /// <remarks>
    /// A turn onto a leg and the turn off it each consume about one radius of it, so two radii is
    /// where the straight section disappears entirely. Four is the point at which the aircraft
    /// actually settles on the leg for a while, which is what makes a route recognisable as the
    /// shape it was drawn as. Advisory, not enforced -- a tighter route is flyable, it is just not
    /// the one on the map, and refusing to fly it would be this type deciding what a demo may show.
    /// </remarks>
    internal const double AdvisoryLegTurnRadii = 4.0;

    /// <summary>
    /// How many capture radii a leg has to be before the two waypoints at its ends can be told
    /// apart.
    /// </summary>
    /// <remarks>
    /// Two, and it follows from the triangle inequality rather than from taste. Capture happens
    /// anywhere within one radius of a waypoint, so at that moment the next one is at least
    /// <c>leg - radius</c> away; for that to be outside the capture radius as well, the leg has to
    /// exceed <c>2 x radius</c>. At or below it the aircraft captures again on the next step
    /// regardless of where it flew, which is the livelock at the top of this file wearing different
    /// clothes -- the aircraft is not orbiting one waypoint it cannot reach, it is standing inside
    /// all of them at once.
    /// </remarks>
    internal const double MinimumLegCaptureRadii = 2.0;

    private readonly IReadOnlyList<Waypoint> _route;
    private readonly double _captureRadiusMeters;
    private readonly LocalProjection _projection;

    /// <summary>
    /// Builds the follower and validates the route against the envelope that has to fly it.
    /// </summary>
    /// <param name="route">The waypoints, flown in order and then from the last back to the first.</param>
    /// <param name="captureRadiusMeters">
    /// How close counts as arrived. Must be at least the envelope's turn radius, and small enough
    /// that the route's shortest leg is more than <see cref="MinimumLegCaptureRadii"/> of it --
    /// see the remarks on this type for both.
    /// </param>
    /// <param name="envelope">What the aircraft can do; the source of the turn radius.</param>
    /// <param name="projection">Metres to degrees, about the route's origin.</param>
    /// <exception cref="ArgumentNullException">Any reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The route is too short, or has a degenerate leg.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The capture radius is not usable.</exception>
    internal WaypointFollower(
        IReadOnlyList<Waypoint> route,
        double captureRadiusMeters,
        AircraftEnvelope envelope,
        LocalProjection projection)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(projection);

        //  Two, not one. A single waypoint is a route with no legs, and the aircraft would capture
        //  it and then be commanded toward the point it is standing on for the rest of the flight.
        if (route.Count < 2)
        {
            throw new ArgumentException(
                "A route needs at least two waypoints; with one there is nothing to fly between.",
                nameof(route));
        }

        if (!double.IsFinite(captureRadiusMeters) || captureRadiusMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureRadiusMeters),
                captureRadiusMeters,
                "The capture radius must be a finite, positive number of metres.");
        }

        if (captureRadiusMeters < envelope.TurnRadiusMeters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureRadiusMeters),
                captureRadiusMeters,
                $"The capture radius must be at least the aircraft's "
                + $"{envelope.TurnRadiusMeters:0.#} m turn radius, which follows from a cruise "
                + $"speed of {envelope.CruiseSpeedMetersPerSecond:0.##} m/s and a maximum bank of "
                + $"{envelope.MaxBankAngleDegrees:0.#} degrees. Below it the aircraft can orbit a "
                + "waypoint it is never able to reach.");
        }

        _route = route;
        _captureRadiusMeters = captureRadiusMeters;
        _projection = projection;

        ShortestLegMeters = MeasureShortestLeg(route, projection);

        if (ShortestLegMeters < MinimumLegMeters)
        {
            throw new ArgumentException(
                $"The route has a leg shorter than {MinimumLegMeters} m, which has no usable "
                + "bearing. Two consecutive waypoints at the same place is the usual cause.",
                nameof(route));
        }

        //  Checked after the shortest leg is measured, so a degenerate route is reported as a
        //  degenerate route rather than blamed on a capture radius that is only wrong beside it.
        double minimumLegMeters = captureRadiusMeters * MinimumLegCaptureRadii;

        if (ShortestLegMeters <= minimumLegMeters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureRadiusMeters),
                captureRadiusMeters,
                $"A capture radius of {captureRadiusMeters:0.#} m needs every leg to be longer "
                + $"than {minimumLegMeters:0.#} m, and this route's shortest is "
                + $"{ShortestLegMeters:0.#} m. Under that the aircraft is inside the next "
                + "waypoint's capture radius the moment it reaches this one, so it captures the "
                + "whole route on consecutive steps and flies none of it.");
        }
    }

    /// <summary>Gets the index of the waypoint currently being flown to.</summary>
    internal int ActiveIndex { get; private set; }

    /// <summary>Gets how many waypoints the route has.</summary>
    internal int WaypointCount => _route.Count;

    /// <summary>Gets how close to a waypoint counts as having reached it, in metres.</summary>
    /// <remarks>
    /// Read back rather than only passed in, because it is often derived from the turn radius
    /// rather than configured, and the startup log line has to report the number actually in force.
    /// </remarks>
    internal double CaptureRadiusMeters => _captureRadiusMeters;

    /// <summary>
    /// Gets how many times the route has been completed, incremented when the active index wraps
    /// back to the first waypoint.
    /// </summary>
    /// <remarks>
    /// Exposed for the test that flies two laps and asserts the second is the first again. It is
    /// the only observable that distinguishes a route that loops from one that stopped.
    /// </remarks>
    internal int LapCount { get; private set; }

    /// <summary>Gets the length of the shortest leg in the route, in metres.</summary>
    /// <remarks>
    /// Measured here because this is the type that knows what a leg is, and read twice against two
    /// different bounds. Against <see cref="MinimumLegCaptureRadii"/> capture radii it is enforced
    /// in the constructor, because below that the route is not flown at all. Against
    /// <see cref="AdvisoryLegTurnRadii"/> turn radii it is only exposed, for the host to warn
    /// about, because a route that merely rounds off its corners is a choice rather than an error.
    /// </remarks>
    internal double ShortestLegMeters { get; }

    /// <summary>Gets the waypoint currently being flown to.</summary>
    internal Waypoint ActiveWaypoint => _route[ActiveIndex];

    /// <summary>
    /// Captures the active waypoint if the aircraft has reached it, and returns what to fly next.
    /// </summary>
    /// <param name="state">Where the aircraft is now.</param>
    internal FlightCommand Update(AircraftState state)
    {
        double distance = _projection.GroundDistanceMeters(
            state.LatitudeDegrees,
            state.LongitudeDegrees,
            ActiveWaypoint.LatitudeDegrees,
            ActiveWaypoint.LongitudeDegrees);

        if (distance <= _captureRadiusMeters)
        {
            ActiveIndex = (ActiveIndex + 1) % _route.Count;

            //  The seam. Nothing else resets here -- not the position, not the heading, not the
            //  altitude -- which is what makes the route loop rather than restart, and what the
            //  continuity test checks by looking for a step longer than one tick's travel.
            if (ActiveIndex == 0)
            {
                LapCount++;
            }
        }

        Waypoint target = ActiveWaypoint;

        return new FlightCommand(
            _projection.BearingDegrees(
                state.LatitudeDegrees,
                state.LongitudeDegrees,
                target.LatitudeDegrees,
                target.LongitudeDegrees),
            target.AltitudeMetersMsl);
    }

    /// <summary>
    /// Returns the shortest leg in the closed route, including the one from the last waypoint back
    /// to the first.
    /// </summary>
    /// <remarks>
    /// The closing leg is included because the route loops: leaving it out would let a route be
    /// drawn with a long outbound path and a returning leg of two metres, and report the shape as
    /// fine.
    /// </remarks>
    private static double MeasureShortestLeg(
        IReadOnlyList<Waypoint> route, LocalProjection projection)
    {
        double shortest = double.PositiveInfinity;

        for (int i = 0; i < route.Count; i++)
        {
            Waypoint from = route[i];
            Waypoint to = route[(i + 1) % route.Count];

            shortest = Math.Min(
                shortest,
                projection.GroundDistanceMeters(
                    from.LatitudeDegrees,
                    from.LongitudeDegrees,
                    to.LatitudeDegrees,
                    to.LongitudeDegrees));
        }

        return shortest;
    }
}
