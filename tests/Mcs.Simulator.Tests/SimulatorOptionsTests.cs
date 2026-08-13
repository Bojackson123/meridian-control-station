using System.ComponentModel.DataAnnotations;

using Mcs.Simulator.Flight;

namespace Mcs.Simulator.Tests;

/// <summary>
/// Startup validation: configuration that would fly a plausible-looking wrong aircraft is refused,
/// with the offending setting named.
/// </summary>
/// <remarks>
/// <b>Rejected, never clamped.</b> Every case below has a "helpful" alternative that produces an
/// aircraft which flies, transmits and looks fine -- a capture radius nudged up to something
/// workable, a bank angle trimmed to something survivable, a missing route replaced by a default
/// circle. Each of those hands an operator a picture that does not correspond to what they
/// configured, which is the hazard the whole station is arranged against.
/// </remarks>
public sealed class SimulatorOptionsTests
{
    /// <summary>The shipped defaults pass their own validation.</summary>
    /// <remarks>
    /// First, because everything below is an argument about which departures from them fail, and a
    /// suite whose baseline is already invalid proves nothing about any of them.
    /// </remarks>
    [Fact]
    public void TheShippedDefaults_Validate()
    {
        Assert.Empty(Validate(TestAircraft.Options()));
    }

    /// <summary>An empty or one-waypoint route is refused, naming the route.</summary>
    [Fact]
    public void ARouteWithFewerThanTwoWaypoints_IsRejected()
    {
        SimulatorOptions options = TestAircraft.Options();
        options.Route = [TestAircraft.RouteOptions()[0]];

        ValidationResult failure = Assert.Single(Validate(options));

        Assert.Contains(nameof(SimulatorOptions.Route), failure.MemberNames);
        Assert.Contains("two waypoints", failure.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture radius under the turn radius is refused, and the message says what it must beat.
    /// </summary>
    /// <remarks>
    /// The number it has to beat is not a setting -- it is derived from the cruise speed and the
    /// bank limit -- so an operator who is only shown "invalid" has nothing to change it to. The
    /// message carrying the derived radius is the point of the assertion, not decoration.
    /// </remarks>
    [Fact]
    public void ACaptureRadiusUnderTheTurnRadius_IsRejected()
    {
        SimulatorOptions options = TestAircraft.Options();
        AircraftEnvelope envelope = options.CreateEnvelope();

        options.CaptureRadiusMeters = envelope.TurnRadiusMeters * 0.5;

        ValidationResult failure = Assert.Single(Validate(options));

        Assert.Contains(nameof(SimulatorOptions.CaptureRadiusMeters), failure.MemberNames);
        Assert.Contains("turn radius", failure.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>An unset capture radius is derived from the turn radius rather than defaulted.</summary>
    /// <remarks>
    /// It has to come out above the turn radius or the derivation would be shipping the very
    /// configuration the check above rejects.
    /// </remarks>
    [Fact]
    public void AnUnsetCaptureRadius_IsDerivedFromTheTurnRadius()
    {
        SimulatorOptions options = TestAircraft.Options();
        AircraftEnvelope envelope = options.CreateEnvelope();

        Assert.Null(options.CaptureRadiusMeters);
        Assert.True(options.ResolveCaptureRadiusMeters(envelope) > envelope.TurnRadiusMeters);
    }

    /// <summary>
    /// A message rate above the step rate is refused, naming the stream and the step it lost to.
    /// </summary>
    /// <remarks>
    /// The schedules are polled once per simulation step and fire at most once per poll, so a rate
    /// above the step is not fast, it is the step rate with the difference discarded. Left to
    /// emerge it is invisible from everywhere: the frames are well formed, the counters agree with
    /// the frames, and the only symptom is a console updating more slowly than the configuration
    /// says -- which reads as a slow link, and would be acted on as one.
    /// </remarks>
    [Fact]
    public void AMessageRateAboveTheStepRate_IsRejected()
    {
        SimulatorOptions options = TestAircraft.Options();
        options.GlobalPositionHz = options.StepHz + 10.0;

        ValidationResult failure = Assert.Single(Validate(options));

        Assert.Contains(nameof(SimulatorOptions.GlobalPositionHz), failure.MemberNames);
        Assert.Contains(nameof(SimulatorOptions.StepHz), failure.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture radius that reaches from one waypoint to the next is refused, naming the route.
    /// </summary>
    /// <remarks>
    /// Blamed on the route rather than the radius when the radius was not configured -- here it is,
    /// so the radius is what an operator can change and the radius is what the failure names. The
    /// derived default is safe against the shipped circuit by a wide margin, which is exactly why
    /// nothing would have caught a configured one.
    /// </remarks>
    [Fact]
    public void ACaptureRadiusThatSwallowsTheLegs_IsRejected()
    {
        SimulatorOptions options = TestAircraft.Options();
        options.CaptureRadiusMeters = 500.0;

        ValidationResult failure = Assert.Single(Validate(options));

        Assert.Contains(nameof(SimulatorOptions.CaptureRadiusMeters), failure.MemberNames);
        Assert.Contains("shortest", failure.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>Two waypoints in the same place are refused: the leg between them has no bearing.</summary>
    [Fact]
    public void ARouteWithADegenerateLeg_IsRejected()
    {
        SimulatorOptions options = TestAircraft.Options();
        options.Route = [options.Route[0], options.Route[0], options.Route[2]];

        ValidationResult failure = Assert.Single(Validate(options));

        Assert.Contains(nameof(SimulatorOptions.Route), failure.MemberNames);
    }

    /// <summary>A waypoint that is not a place is refused, and the message carries the value.</summary>
    [Fact]
    public void ARouteWithAnOutOfRangeWaypoint_IsRejected()
    {
        SimulatorOptions options = TestAircraft.Options();
        options.Route[1].LongitudeDegrees = 200.0;

        ValidationResult failure = Assert.Single(Validate(options));

        Assert.Contains(nameof(SimulatorOptions.Route), failure.MemberNames);
        Assert.Contains("200", failure.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bank angle at or past vertical is refused, because the turn radius collapses toward zero.
    /// </summary>
    /// <remarks>
    /// The attribute range on the property catches this first in the host, where the whole
    /// <c>ValidateDataAnnotations</c> pipeline runs. Asserted against the envelope directly because
    /// this test drives <see cref="IValidatableObject.Validate"/> alone -- and because the envelope
    /// is what the flight model actually depends on, so it has to refuse independently rather than
    /// trusting an attribute on a type it does not reference.
    /// </remarks>
    [Fact]
    public void ABankAngleAtVertical_IsRejectedByTheEnvelope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TestAircraft.Envelope(maxBank: 90.0));
    }

    /// <summary>A cruise speed of zero is refused: an aircraft that never moves.</summary>
    [Fact]
    public void ACruiseSpeedOfZero_IsRejectedByTheEnvelope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TestAircraft.Envelope(cruiseSpeed: 0));
    }

    /// <summary>
    /// Runs only <see cref="SimulatorOptions.Validate"/>, which is where the cross-setting checks
    /// live.
    /// </summary>
    /// <remarks>
    /// The per-property <c>[Range]</c> attributes are the host's job and are covered by the fact
    /// that <c>ValidateDataAnnotations</c> is wired up at all; what is worth testing here is the
    /// set of failures that no single property can express.
    /// </remarks>
    private static IReadOnlyList<ValidationResult> Validate(SimulatorOptions options) =>
        [.. options.Validate(new ValidationContext(options))];
}
