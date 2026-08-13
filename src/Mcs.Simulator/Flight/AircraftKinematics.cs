namespace Mcs.Simulator.Flight;

/// <summary>
/// Advances an <see cref="AircraftState"/> by one step, holding it inside the
/// <see cref="AircraftEnvelope"/>: a bank-limited turn toward the commanded heading, a rate-limited
/// change toward the commanded altitude, and the position that follows from flying the result.
/// </summary>
/// <remarks>
/// <b>Not a physics engine, and the omissions are the design.</b> There is no wind, no drag, no
/// mass and no attitude beyond heading. What is modelled is exactly the set of things that would
/// make a station believe something false if they were wrong: the aircraft never teleports, its
/// speed matches the distance between two consecutive positions, and its heading cannot change
/// faster than a bank angle allows.
///
/// <para>
/// <b>The turn is the property worth defending.</b> Heading moves by at most
/// <see cref="AircraftEnvelope.MaxTurnRateDegreesPerSecond"/> per second, which is
/// <c>v / R</c> for the radius <c>v²/(g·tan φ)</c> the envelope derived. That is why nothing here
/// takes a turn rate as a parameter: the number is a consequence of a speed and a bank angle, and
/// a deconfliction margin computed from the same two must describe the aircraft that actually
/// flew.
/// </para>
///
/// <para>
/// <b>Position integrates from the heading <i>after</i> the turn, not before.</b> Both are a
/// first-order approximation of a curved path by a straight segment, and over a 50 ms step the
/// difference is millimetres. Using the new heading is chosen because it keeps the reported
/// heading and the reported movement consistent with each other: a state that says "heading 090,
/// and I just moved north-east" is the kind of small incoherence a console makes visible by
/// drawing both.
/// </para>
///
/// <para>
/// <b>Stateless and pure.</b> It holds an envelope and a projection, both immutable, and returns a
/// new state rather than mutating one -- so a test can integrate ten thousand steps of a flight
/// path with no clock, no socket and no host, which is what makes the turn radius assertable
/// against a closed-form answer instead of against a recording.
/// </para>
/// </remarks>
/// <param name="envelope">What the aircraft can do.</param>
/// <param name="projection">Metres to degrees, about the route's origin.</param>
internal sealed class AircraftKinematics(AircraftEnvelope envelope, LocalProjection projection)
{
    /// <summary>Gets the envelope every step is held inside.</summary>
    internal AircraftEnvelope Envelope => envelope;

    /// <summary>
    /// Returns the state one step later, flying toward <paramref name="command"/>.
    /// </summary>
    /// <param name="state">Where the aircraft is now.</param>
    /// <param name="command">The heading and altitude the follower is asking for.</param>
    /// <param name="step">
    /// How much time passes. Positive: a zero step is a state that cannot change and a negative one
    /// flies the aircraft backwards, neither of which is a useful way to discover a subtraction was
    /// the wrong way round.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not positive.</exception>
    internal AircraftState Advance(AircraftState state, FlightCommand command, TimeSpan step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);

        double seconds = step.TotalSeconds;

        //  The short way round the compass. A raw subtraction turns a 20-degree correction across
        //  north into a 340-degree one, and the aircraft spends a minute going the wrong way to
        //  arrive at the same place -- see LocalProjection.SignedDifferenceDegrees.
        double headingError =
            LocalProjection.SignedDifferenceDegrees(state.HeadingDegrees, command.HeadingDegrees);

        double maxTurn = envelope.MaxTurnRateDegreesPerSecond * seconds;
        double turn = Math.Clamp(headingError, -maxTurn, maxTurn);

        double heading = LocalProjection.NormaliseDegrees(state.HeadingDegrees + turn);

        //  Climb and descent are limited separately, so the sign of the error selects the limit.
        //  An aircraft is not symmetric about level flight and using one number for both would make
        //  every descent as slow as the climb it was configured from.
        double altitudeError = command.AltitudeMetersMsl - state.AltitudeMetersMsl;
        double maxRise = envelope.MaxClimbRateMetersPerSecond * seconds;
        double maxFall = envelope.MaxDescentRateMetersPerSecond * seconds;
        double altitudeChange = Math.Clamp(altitudeError, -maxFall, maxRise);

        //  Reported as a rate rather than the raw change, because that is what a vehicle publishes
        //  and what the climb-limit test compares against the envelope. Dividing back out by the
        //  step is exact enough here: both came from the same double.
        double climbRate = altitudeChange / seconds;

        double distance = envelope.CruiseSpeedMetersPerSecond * seconds;
        double headingRadians = double.DegreesToRadians(heading);

        //  North takes the cosine and east the sine: a compass bearing is measured from north, the
        //  transpose of the usual convention. The other way round flies the aircraft along a path
        //  mirrored about the north-east diagonal, which looks like a route rather than a bug.
        (double latitude, double longitude) = projection.Offset(
            state.LatitudeDegrees,
            state.LongitudeDegrees,
            distance * Math.Cos(headingRadians),
            distance * Math.Sin(headingRadians));

        return state with
        {
            LatitudeDegrees = latitude,
            LongitudeDegrees = longitude,
            AltitudeMetersMsl = state.AltitudeMetersMsl + altitudeChange,
            HeadingDegrees = heading,
            GroundSpeedMetersPerSecond = envelope.CruiseSpeedMetersPerSecond,
            ClimbRateMetersPerSecond = climbRate,

            //  Floored, never wrapped. A battery that climbs while the aircraft flies is a reading
            //  an operator has to learn to disbelieve, and monotonicity is the one property a
            //  viewer can check from two consecutive frames.
            BatteryPercent = Math.Max(
                0, state.BatteryPercent - (envelope.BatteryDrainPercentPerSecond * seconds)),
        };
    }
}
