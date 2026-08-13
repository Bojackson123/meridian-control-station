using Mcs.Adapters.Mavlink;
using Mcs.Adapters.Mavlink.Messages;
using Mcs.Core;
using Mcs.Simulator.Flight;
using Mcs.Simulator.Mavlink;

namespace Mcs.Simulator.Tests;

/// <summary>
/// The loop closes: bytes this simulator emits are read by the station's own parser and decoder,
/// and the telemetry that comes out is the aircraft that went in.
/// </summary>
/// <remarks>
/// <b>What this proves, and what it explicitly does not.</b> The payload writers in
/// <c>Mcs.Simulator</c> and the message readers in <c>Mcs.Adapters</c> were written independently
/// against the message definitions, so a field at the wrong offset, a byte order the wrong way
/// round, or a scale factor out by a thousand in either one shows up here as a number that does not
/// match. That is real evidence about the payloads.
///
/// <para>
/// It is <b>not</b> evidence about framing. Both sides share <c>MavlinkFrameWriter</c> and
/// <c>MavlinkFrameParser</c>, so a transposed CRC seed, a checksum over the wrong span or a
/// truncation rule off by one would cancel exactly and this test would still pass. The committed
/// pymavlink byte vectors in <c>Mcs.Adapters.Tests</c> remain the only evidence for framing, and
/// nothing here is offered as a substitute for them.
/// </para>
///
/// <para>
/// One frame per datagram, appended and drained one at a time, because that is what the transmitter
/// does. The adapter's own suite covers the datagram carrying three frames.
/// </para>
/// </remarks>
public sealed class StationDecodesTheSimulatorTests
{
    /// <summary>Latitude and longitude survive the 1e7 scaling to well under a metre.</summary>
    private const double DegreeTolerance = 1e-6;

    /// <summary>Altitude survives the millimetre scaling.</summary>
    private const double AltitudeToleranceMeters = 0.01;

    /// <summary>
    /// VFR_HUD carries heading in whole degrees, so half a degree of rounding is the floor here.
    /// </summary>
    private const double HeadingToleranceDegrees = 0.51;

    /// <summary>
    /// The station reads the aircraft's position, altitude, speed, heading and battery back.
    /// </summary>
    /// <remarks>
    /// Each field is compared against the state of the message it actually came from, which is the
    /// substance of the test rather than a detail of it. The station composes one report from
    /// several messages on different schedules: position and altitude from the GLOBAL_POSITION_INT
    /// that triggered the report, speed and heading from the most recent VFR_HUD, battery from the
    /// most recent SYS_STATUS. Comparing everything against the position's own state would fail for
    /// the right reason and be indistinguishable from failing for the wrong one.
    /// </remarks>
    [Fact]
    public void EmittedFrames_DecodeBackIntoTheAircraftThatSentThem()
    {
        List<EmittedFrame> emitted = new SimulatedFlight().Fly(30.0);

        //  What each report should contain, in the order the reports will arrive: built by walking
        //  the frames the way the station's assembler walks them.
        List<ExpectedReport> expected = [];
        AircraftState? lastHud = null;
        AircraftState? lastSysStatus = null;

        foreach (EmittedFrame frame in emitted)
        {
            switch (frame.MessageId)
            {
                case VehicleMessageId.VfrHud:
                    lastHud = frame.State;
                    break;

                case VehicleMessageId.SysStatus:
                    lastSysStatus = frame.State;
                    break;

                case VehicleMessageId.GlobalPositionInt:
                    expected.Add(new ExpectedReport(frame.State, lastHud, lastSysStatus));
                    break;

                default:
                    break;
            }
        }

        MavlinkFrameParser parser = new();
        MavlinkTelemetryDecoder decoder = new();
        List<VehicleTelemetry> reports = [];

        foreach (EmittedFrame frame in emitted)
        {
            parser.Append(frame.Bytes);

            while (parser.TryReadFrame(out MavlinkFrame? parsed))
            {
                if (decoder.TryDecode(parsed, out VehicleTelemetry? telemetry))
                {
                    reports.Add(telemetry);
                }
            }
        }

        Assert.NotEmpty(reports);
        Assert.Equal(expected.Count, reports.Count);

        for (int i = 0; i < reports.Count; i++)
        {
            VehicleTelemetry report = reports[i];
            ExpectedReport source = expected[i];

            Assert.Equal(source.Position.LatitudeDegrees, report.LatitudeDegrees, DegreeTolerance);
            Assert.Equal(source.Position.LongitudeDegrees, report.LongitudeDegrees, DegreeTolerance);

            //  MSL, declared. The station pairs the number with its reference at the decode, so a
            //  report carrying a bare altitude would not have got this far.
            Assert.Equal(AltitudeReference.Msl, report.Altitude.Reference);
            Assert.Equal(
                source.Position.AltitudeMetersMsl, report.Altitude.Meters, AltitudeToleranceMeters);

            Assert.NotNull(source.Hud);
            Assert.Equal(
                source.Hud.Value.GroundSpeedMetersPerSecond,
                report.GroundSpeedMetersPerSecond!.Value,
                0.01);

            Assert.Equal(
                0,
                Math.Abs(
                    LocalProjection.SignedDifferenceDegrees(
                        source.Hud.Value.HeadingDegrees, report.HeadingDegrees!.Value)),
                HeadingToleranceDegrees);

            Assert.NotNull(source.SysStatus);
            Assert.Equal(source.SysStatus.Value.BatteryPercent, report.BatteryPercent!.Value, 0.51);
        }
    }

    /// <summary>
    /// Nothing the simulator sends is discarded, rejected, or arrives without the HUD behind it.
    /// </summary>
    /// <remarks>
    /// The counters are the assertion here rather than a diagnostic. Each one names a way the
    /// station throws traffic away, and on a link carrying nothing but this vehicle every one of
    /// them must stay at zero -- a simulator whose frames are half discarded would still produce
    /// correct-looking telemetry from the half that survived, and the test above would pass.
    /// <para>
    /// <c>PositionsWithoutHud</c> at zero also pins the emitter's ordering: the first HUD goes out
    /// in the same step as the first position, ahead of it, so no report is ever built with the
    /// speed and heading missing.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStationDiscardsNothingTheSimulatorSends()
    {
        List<EmittedFrame> emitted = new SimulatedFlight().Fly(30.0);

        MavlinkFrameParser parser = new();
        MavlinkTelemetryDecoder decoder = new();
        int positions = 0;

        foreach (EmittedFrame frame in emitted)
        {
            if (frame.MessageId == VehicleMessageId.GlobalPositionInt)
            {
                positions++;
            }

            parser.Append(frame.Bytes);

            while (parser.TryReadFrame(out MavlinkFrame? parsed))
            {
                decoder.TryDecode(parsed, out _);
            }
        }

        MavlinkParserStatistics framing = parser.Statistics;

        Assert.Equal(emitted.Count, framing.FramesParsed);
        Assert.Equal(0, framing.ChecksumFailures);
        Assert.Equal(0, framing.BytesResynced);
        Assert.Equal(0, framing.UnknownMessagesSkipped);
        Assert.Equal(0, framing.V1FramesSkipped);
        Assert.Equal(0, framing.SignedFramesRejected);
        Assert.Equal(0, framing.IncompatibleFlagsRejected);

        MavlinkDecoderStatistics decode = decoder.Statistics;

        Assert.Equal(emitted.Count, decode.MessagesDecoded);
        Assert.Equal(0, decode.MessagesRejected);
        Assert.Equal(positions, decode.TelemetryEmitted);
        Assert.Equal(0, decode.PositionsWithoutHud);

        //  One vehicle, one component: the station saw exactly one sender.
        Assert.Equal(1, decoder.SenderCount);
    }

    /// <summary>What one telemetry report should contain, and which message each field came from.</summary>
    private readonly record struct ExpectedReport(
        AircraftState Position, AircraftState? Hud, AircraftState? SysStatus);
}
