using Mcs.Simulator.Mavlink;

namespace Mcs.Simulator.Tests;

/// <summary>
/// What the vehicle puts on the wire: four streams at four rates, sequence numbers that wrap, and
/// bytes the station can read.
/// </summary>
public sealed class VehicleMessageEmitterTests
{
    private const double FlightSeconds = 60.0;

    /// <summary>
    /// The four message types arrive at four different rates, in the proportions configured.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that fails if someone folds the schedules into one bundle.</b> The
    /// station's assembler exists because a vehicle sends its messages on independent schedules --
    /// it composes a running state from several and emits when a position lands -- and a simulator
    /// that sent everything together at one rate would leave that code untested by construction
    /// while every other test here still passed. Nothing in the frame counts alone would say so
    /// either: the total would be unchanged.
    /// </remarks>
    [Fact]
    public void Messages_ArriveAtFourGenuinelyDifferentRates()
    {
        SimulatedFlight flight = new();
        List<EmittedFrame> emitted = flight.Fly(FlightSeconds);

        int heartbeats = Count(emitted, VehicleMessageId.Heartbeat);
        int sysStatuses = Count(emitted, VehicleMessageId.SysStatus);
        int vfrHuds = Count(emitted, VehicleMessageId.VfrHud);
        int positions = Count(emitted, VehicleMessageId.GlobalPositionInt);

        //  Every stream fires once at t = 0 and then on its own interval, so the expected count is
        //  rate x duration plus that first frame. Two either side, because the last interval of the
        //  flight may or may not land inside it depending on where the step boundary falls.
        AssertRate(heartbeats, flight.Rates.HeartbeatHz, nameof(VehicleMessageId.Heartbeat));
        AssertRate(sysStatuses, flight.Rates.SysStatusHz, nameof(VehicleMessageId.SysStatus));
        AssertRate(vfrHuds, flight.Rates.VfrHudHz, nameof(VehicleMessageId.VfrHud));
        AssertRate(
            positions, flight.Rates.GlobalPositionHz, nameof(VehicleMessageId.GlobalPositionInt));

        //  Four distinct numbers, stated as its own assertion: the counts above could each be
        //  within tolerance of a rate that another stream also happens to be sending at.
        Assert.Equal(4, new HashSet<int> { heartbeats, sysStatuses, vfrHuds, positions }.Count);

        //  And the counters agree with the frames, so a reader of the log is reading the link.
        Assert.Equal(heartbeats, flight.Statistics.HeartbeatsSent);
        Assert.Equal(sysStatuses, flight.Statistics.SysStatusesSent);
        Assert.Equal(vfrHuds, flight.Statistics.VfrHudsSent);
        Assert.Equal(positions, flight.Statistics.PositionsSent);
    }

    /// <summary>
    /// The VFR_HUD rate is not a divisor of the position rate, so both of the assembler's paths run.
    /// </summary>
    /// <remarks>
    /// A separate assertion from the rates themselves because it is a claim about the defaults
    /// rather than about the mechanism: at 3 Hz against 4 Hz most positions arrive with no HUD in
    /// the same instant, which is what exercises the station's carry-forward of the previous one.
    /// Set the HUD to 4 Hz and every position would carry a fresh HUD, and the path that matters
    /// on a real link -- where the two rates are never locked -- would never run here.
    /// </remarks>
    [Fact]
    public void PositionAndHudRates_AreNotHarmonic()
    {
        MessageRates rates = new SimulatedFlight().Rates;

        //  Both directions, because harmonic is not a property of one ordering. A HUD at 8 Hz
        //  against positions at 4 Hz is exactly the locked pair this test exists to reject, and its
        //  position-over-HUD ratio is 0.5 -- not a whole number, so a single-direction check calls
        //  it fine.
        Assert.False(
            DividesEvenly(rates.GlobalPositionHz, rates.VfrHudHz),
            $"{rates.GlobalPositionHz} Hz of positions is a whole multiple of {rates.VfrHudHz} Hz "
            + "of HUDs, so every position would carry a HUD from its own instant.");

        Assert.False(
            DividesEvenly(rates.VfrHudHz, rates.GlobalPositionHz),
            $"{rates.VfrHudHz} Hz of HUDs is a whole multiple of {rates.GlobalPositionHz} Hz of "
            + "positions, so the two are locked and the carry-forward never varies.");
    }

    /// <summary>Sequence numbers increment once per frame and wrap at 255.</summary>
    /// <remarks>
    /// Cheap to check and invisible until something starts counting drops, at which point a wrong
    /// wrap reads as a link losing frames. The flight is long enough to pass 255 twice, so a
    /// counter that saturated rather than wrapped would show up as well.
    /// </remarks>
    [Fact]
    public void SequenceNumbers_IncrementPerFrameAndWrapAt255()
    {
        List<EmittedFrame> emitted = new SimulatedFlight().Fly(FlightSeconds);

        Assert.True(
            emitted.Count > 512,
            $"Expected more than two wraps' worth of frames; got {emitted.Count}.");

        for (int i = 0; i < emitted.Count; i++)
        {
            //  Byte 4 of a v2 frame is the sequence. Read from the bytes, because the counter's
            //  own value proves nothing about what was written into the header.
            Assert.Equal((byte)(i % 256), emitted[i].Bytes[4]);
        }
    }

    /// <summary>Every frame carries the configured system and component id.</summary>
    /// <remarks>
    /// The system id is what the station turns into a vehicle id, so getting it wrong renames the
    /// aircraft; the component id is half the key the station files senders under, so getting it
    /// wrong splits one vehicle into two states that disagree.
    /// </remarks>
    [Fact]
    public void Frames_CarryTheConfiguredSenderIdentity()
    {
        const byte SystemId = 7;
        const byte ComponentId = 190;

        List<EmittedFrame> emitted =
            new SimulatedFlight(systemId: SystemId, componentId: ComponentId).Fly(5.0);

        Assert.NotEmpty(emitted);
        Assert.All(emitted, frame => Assert.Equal(SystemId, frame.Bytes[5]));
        Assert.All(emitted, frame => Assert.Equal(ComponentId, frame.Bytes[6]));
    }

    /// <summary>Whether one rate is a whole multiple of another.</summary>
    /// <remarks>
    /// A tolerance rather than a comparison against a rounded value. Rates are doubles, so a
    /// quotient that is a whole number on paper need not be one in the last bits -- and
    /// <see cref="Math.Round(double)"/> rounds a half to even, which makes 0.5 round to 0 and an
    /// equality check against it pass for exactly the pair that should fail.
    /// </remarks>
    private static bool DividesEvenly(double rateHz, double otherRateHz)
    {
        double quotient = rateHz / otherRateHz;

        return Math.Abs(quotient - Math.Round(quotient)) < 1e-9;
    }

    private static int Count(List<EmittedFrame> emitted, uint messageId) =>
        emitted.Count(frame => frame.MessageId == messageId);

    private static void AssertRate(int actual, double rateHz, string messageName)
    {
        int expected = (int)Math.Round(rateHz * FlightSeconds) + 1;

        Assert.InRange(actual, expected - 2, expected + 2);
        Assert.True(actual > 0, $"{messageName} was never sent.");
    }
}
