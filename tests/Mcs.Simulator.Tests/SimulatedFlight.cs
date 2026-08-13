using Mcs.Simulator.Flight;
using Mcs.Simulator.Mavlink;

namespace Mcs.Simulator.Tests;

/// <summary>One frame the vehicle emitted, and the aircraft state it was built from.</summary>
/// <param name="MessageId">Read back out of the frame header, so the pairing is the wire's own.</param>
/// <param name="Bytes">The complete MAVLink v2 frame.</param>
/// <param name="State">Where the aircraft was when this frame was built.</param>
internal readonly record struct EmittedFrame(uint MessageId, byte[] Bytes, AircraftState State);

/// <summary>
/// Flies the aircraft and records every frame it emitted alongside the state that produced it.
/// </summary>
/// <remarks>
/// <b>No clock, no socket, no host.</b> The service that runs this in production owns all three;
/// what it does with them is a timer and a <c>SendToAsync</c>, and neither is a thing worth
/// asserting about here. What is worth asserting about is the relationship between the aircraft's
/// state and the bytes that describe it, and that relationship is entirely inside the emitter.
/// <para>
/// The step loop is deliberately the same shape as
/// <c>SimulatedVehicleService.ExecuteAsync</c>'s -- follower, then kinematics, then emitter, with
/// elapsed time as a tick count times the step. A harness that stepped them in a different order
/// would be testing a vehicle that does not exist.
/// </para>
/// </remarks>
internal sealed class SimulatedFlight
{
    private readonly double _stepSeconds;
    private readonly TimeSpan _step;
    private readonly WaypointFollower _follower;
    private readonly AircraftKinematics _kinematics;
    private readonly VehicleMessageEmitter _emitter;

    private AircraftState _state = TestAircraft.InitialState();
    private long _ticks;

    /// <summary>Builds a flight with the shipped defaults, or the rates a test wants instead.</summary>
    internal SimulatedFlight(
        MessageRates? rates = null,
        double stepHz = 20.0,
        byte systemId = 1,
        byte componentId = 1)
    {
        AircraftEnvelope envelope = TestAircraft.Envelope();
        LocalProjection projection = TestAircraft.Projection();
        IReadOnlyList<Waypoint> route = TestAircraft.Route();

        _follower = new WaypointFollower(
            route, envelope.TurnRadiusMeters * 1.5, envelope, projection);

        _kinematics = new AircraftKinematics(envelope, projection);

        _step = TimeSpan.FromSeconds(1.0 / stepHz);
        _stepSeconds = _step.TotalSeconds;

        Rates = rates ?? new MessageRates(
            HeartbeatHz: 1.0, SysStatusHz: 0.5, VfrHudHz: 3.0, GlobalPositionHz: 4.0);

        _emitter = new VehicleMessageEmitter(
            systemId, componentId, Rates, route[0].AltitudeMetersMsl, Statistics);
    }

    /// <summary>Gets the rates this flight is emitting at.</summary>
    internal MessageRates Rates { get; }

    /// <summary>Gets the counters the emitter fills in.</summary>
    internal SimulatorStatistics Statistics { get; } = new();

    /// <summary>Flies for <paramref name="seconds"/> of simulated time, returning what it emitted.</summary>
    internal List<EmittedFrame> Fly(double seconds)
    {
        List<EmittedFrame> emitted = [];
        long steps = (long)Math.Round(seconds / _stepSeconds);

        for (long i = 0; i < steps; i++)
        {
            _ticks++;
            double elapsedSeconds = _ticks * _stepSeconds;

            FlightCommand command = _follower.Update(_state);
            _state = _kinematics.Advance(_state, command, _step);

            foreach (byte[] frame in _emitter.FramesDue(elapsedSeconds, _state))
            {
                emitted.Add(new EmittedFrame(MessageIdOf(frame), frame, _state));
            }
        }

        return emitted;
    }

    /// <summary>Reads the 24-bit message id out of a v2 frame header.</summary>
    /// <remarks>
    /// Taken from the bytes rather than recorded as the emitter builds them, so the pairing between
    /// a frame and what it claims to be is the one a receiver would make. An emitter that wrote the
    /// wrong id into the header would otherwise still be labelled correctly here.
    /// </remarks>
    private static uint MessageIdOf(byte[] frame) =>
        frame[7] | ((uint)frame[8] << 8) | ((uint)frame[9] << 16);
}
