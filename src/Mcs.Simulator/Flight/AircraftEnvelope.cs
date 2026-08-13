using System.Globalization;

namespace Mcs.Simulator.Flight;

/// <summary>
/// What the simulated aircraft can do: how fast it flies, how hard it may bank, how quickly it may
/// change altitude, and how long its battery lasts.
/// </summary>
/// <remarks>
/// <b><see cref="TurnRadiusMeters"/> is derived, and configuring a turn rate directly is the thing
/// this type exists to prevent.</b> The radius of a coordinated level turn is
/// <c>R = v² / (g·tan φ)</c> for airspeed <c>v</c> and bank angle <c>φ</c>. That relation is the
/// one a deconfliction bound is built on: a separation margin has to account for how far an
/// aircraft travels sideways before it can come round, and the only honest way to compute that is
/// from the same envelope the aircraft is actually flown with. A configured turn rate would let the
/// two drift apart silently, so that the margin was computed for an aircraft that no longer exists
/// and the arithmetic still looked right.
/// <para>
/// Everything else here is deliberately thin. There is no wind, no drag, no mass and no airspeed
/// versus ground speed distinction, because the simulator's job is to make the <i>station</i>
/// testable, and a better aircraft does not make the station better. The turn is the one property
/// worth spending time on.
/// </para>
/// <para>
/// Immutable and validated at construction, so a bad setting fails at startup with the offending
/// number named rather than producing an aircraft that flies plausibly and wrongly.
/// </para>
/// </remarks>
internal sealed class AircraftEnvelope
{
    /// <summary>
    /// Standard gravity, in metres per second squared. The <c>g</c> in <c>v²/(g·tan φ)</c>.
    /// </summary>
    /// <remarks>
    /// The defined standard value rather than a local one. It is a constant in a formula that a
    /// separate document will restate, and two places holding 9.81 and 9.80665 disagree by an
    /// amount too small to see and large enough to fail a tight tolerance.
    /// </remarks>
    internal const double StandardGravity = 9.80665;

    /// <summary>
    /// The bank angle at which the turn radius formula divides by a tangent running away to
    /// infinity. A real aircraft has structural limits far below this.
    /// </summary>
    private const double MaxRepresentableBankDegrees = 80.0;

    /// <summary>
    /// The fastest cruise this envelope will describe, in metres per second.
    /// </summary>
    /// <remarks>
    /// Far above any aircraft this simulator is for, and it is here rather than only on the
    /// configuration property because <c>GLOBAL_POSITION_INT</c>'s velocity components are signed
    /// centimetres per second: past 327.67 m/s they wrap, and a receiver is told an aircraft
    /// climbing north is descending south at speed. The bound belongs on the type that decides how
    /// fast the aircraft goes -- an envelope built in code rather than bound from configuration
    /// would otherwise pass every check and put a sign-flipped velocity on the wire. Nothing
    /// scaling into those fields re-checks it, so this constant may not be raised toward 327.67
    /// without giving them a bound of their own.
    /// </remarks>
    internal const double MaxCruiseSpeedMetersPerSecond = 300.0;

    private const double FullBatteryPercent = 100.0;

    /// <summary>
    /// Defines the envelope and derives the turn performance from it.
    /// </summary>
    /// <param name="cruiseSpeedMetersPerSecond">Ground speed, held constant. Finite and positive.</param>
    /// <param name="maxBankAngleDegrees">
    /// The steepest coordinated turn the aircraft will fly. Positive and below the point the
    /// tangent degenerates.
    /// </param>
    /// <param name="maxClimbRateMetersPerSecond">The fastest it will gain altitude. Finite and positive.</param>
    /// <param name="maxDescentRateMetersPerSecond">
    /// The fastest it will lose altitude, as a positive number. Separate from the climb rate
    /// because an aircraft is not symmetric about level flight: it descends faster than it climbs.
    /// </param>
    /// <param name="enduranceSeconds">Seconds from a full battery to a flat one. Finite and positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any argument is non-finite or out of range.</exception>
    internal AircraftEnvelope(
        double cruiseSpeedMetersPerSecond,
        double maxBankAngleDegrees,
        double maxClimbRateMetersPerSecond,
        double maxDescentRateMetersPerSecond,
        double enduranceSeconds)
    {
        //  Zero is rejected with the negatives throughout: every one of these divides something or
        //  bounds something, and a zero speed is an aircraft that never moves, which is the one
        //  symptom the simulator exists to rule out.
        if (!double.IsFinite(cruiseSpeedMetersPerSecond)
            || cruiseSpeedMetersPerSecond <= 0
            || cruiseSpeedMetersPerSecond > MaxCruiseSpeedMetersPerSecond)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cruiseSpeedMetersPerSecond),
                cruiseSpeedMetersPerSecond,
                "The cruise speed must be a finite number of metres per second, above zero and no "
                + $"more than {MaxCruiseSpeedMetersPerSecond}; see the constant for what the upper "
                + "bound protects.");
        }

        if (!double.IsFinite(maxBankAngleDegrees)
            || maxBankAngleDegrees <= 0
            || maxBankAngleDegrees > MaxRepresentableBankDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBankAngleDegrees),
                maxBankAngleDegrees,
                $"The maximum bank angle must be between 0 and {MaxRepresentableBankDegrees} "
                + "degrees, exclusive of zero; the turn radius is v^2/(g*tan(bank)), which "
                + "collapses toward zero as the bank approaches vertical.");
        }

        if (!double.IsFinite(maxClimbRateMetersPerSecond) || maxClimbRateMetersPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxClimbRateMetersPerSecond),
                maxClimbRateMetersPerSecond,
                "The maximum climb rate must be a finite, positive number of metres per second.");
        }

        if (!double.IsFinite(maxDescentRateMetersPerSecond) || maxDescentRateMetersPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDescentRateMetersPerSecond),
                maxDescentRateMetersPerSecond,
                "The maximum descent rate must be a finite, positive number of metres per second. "
                + "It is a magnitude, not a signed rate.");
        }

        if (!double.IsFinite(enduranceSeconds) || enduranceSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(enduranceSeconds),
                enduranceSeconds,
                "The endurance must be a finite, positive number of seconds.");
        }

        CruiseSpeedMetersPerSecond = cruiseSpeedMetersPerSecond;
        MaxBankAngleDegrees = maxBankAngleDegrees;
        MaxClimbRateMetersPerSecond = maxClimbRateMetersPerSecond;
        MaxDescentRateMetersPerSecond = maxDescentRateMetersPerSecond;
        EnduranceSeconds = enduranceSeconds;

        //  R = v^2 / (g * tan(bank)). Written out rather than hidden behind a helper because it is
        //  the one line in this project that another document quotes.
        TurnRadiusMeters =
            cruiseSpeedMetersPerSecond * cruiseSpeedMetersPerSecond
            / (StandardGravity * Math.Tan(double.DegreesToRadians(maxBankAngleDegrees)));

        //  omega = v / R, which is the same relation rearranged rather than a second constant. Both
        //  are exposed because the integrator wants the rate and the route validation wants the
        //  radius, and deriving one from the other at each call site is how they come to disagree.
        MaxTurnRateDegreesPerSecond =
            double.RadiansToDegrees(cruiseSpeedMetersPerSecond / TurnRadiusMeters);

        BatteryDrainPercentPerSecond = FullBatteryPercent / enduranceSeconds;
    }

    /// <summary>Gets the ground speed the aircraft holds, in metres per second.</summary>
    internal double CruiseSpeedMetersPerSecond { get; }

    /// <summary>Gets the steepest coordinated turn the aircraft will fly, in degrees.</summary>
    internal double MaxBankAngleDegrees { get; }

    /// <summary>Gets the fastest rate of altitude gain, in metres per second.</summary>
    internal double MaxClimbRateMetersPerSecond { get; }

    /// <summary>Gets the fastest rate of altitude loss as a positive magnitude, in metres per second.</summary>
    internal double MaxDescentRateMetersPerSecond { get; }

    /// <summary>Gets the seconds from a full battery to a flat one.</summary>
    internal double EnduranceSeconds { get; }

    /// <summary>
    /// Gets the radius of the tightest level turn this aircraft can fly, in metres:
    /// <c>v² / (g·tan φ)</c>.
    /// </summary>
    internal double TurnRadiusMeters { get; }

    /// <summary>Gets the fastest the heading may change, in degrees per second: <c>v / R</c>.</summary>
    internal double MaxTurnRateDegreesPerSecond { get; }

    /// <summary>Gets how much battery percentage is consumed per second of flight.</summary>
    internal double BatteryDrainPercentPerSecond { get; }

    /// <summary>Describes the envelope in one clause, for the startup log line.</summary>
    /// <remarks>
    /// The derived radius is in here rather than only in the settings, because it is the number an
    /// operator has to compare their route against and the one number that is not configured
    /// anywhere.
    /// </remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{CruiseSpeedMetersPerSecond:0.##} m/s at up to {MaxBankAngleDegrees:0.#} degrees of "
            + $"bank, giving a {TurnRadiusMeters:0.#} m turn radius and "
            + $"{MaxTurnRateDegreesPerSecond:0.##} deg/s; climb {MaxClimbRateMetersPerSecond:0.##} "
            + $"m/s, descent {MaxDescentRateMetersPerSecond:0.##} m/s; "
            + $"{EnduranceSeconds:0.#} s endurance");
}
