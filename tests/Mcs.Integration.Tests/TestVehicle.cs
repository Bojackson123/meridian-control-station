using Mcs.Core;

using Microsoft.Extensions.DependencyInjection;

namespace Mcs.Integration.Tests;

/// <summary>
/// Reports telemetry into a running station the way an adapter does, so a test can decide exactly
/// when a vehicle appears and where it is.
/// </summary>
/// <remarks>
/// These tests used to take their frames from the fake vehicle feed the API shipped, turned up to
/// 5 Hz and then waited on. That feed is gone, and reaching for the MAVLink adapter in its place
/// would make a suite about Postgres and the HTTP contract depend on datagram timing to prove the
/// shape of a JSON response. The socket and its datagram boundaries are proved in
/// <c>Mcs.Adapters.Tests</c>, and the whole pipe end to end in <c>Mcs.System.Tests</c> against the
/// simulator; what is left for this suite is the contract, and a test double belongs in the test
/// project rather than in the shipped host.
/// <para>
/// <b>Through <see cref="TelemetryIngest"/>, not around it.</b> <c>TelemetryFrame</c>'s constructor
/// is internal and a receipt is its only caller, so this takes the same two-phase path an adapter
/// takes and <c>ReceivedAtUtc</c> is the station's own clock here exactly as it is in production.
/// Constructing a frame directly is not available, and that is the design working.
/// </para>
/// <para>
/// Waiting on a timer is what it replaces: a test that writes its own frames states its
/// preconditions instead of hoping for them, and the ones that no longer race a feed do not need
/// to be generous about how long they wait.
/// </para>
/// </remarks>
internal sealed class TestVehicle
{
    //  About 9 m of longitude per report at this latitude -- far enough that two consecutive
    //  frames differ in a way no float comparison could confuse, which is what the stream test
    //  asserts on, and small enough to stay a plausible aircraft rather than a teleport.
    private const double LongitudeStepDegrees = 0.0001;

    private readonly ITelemetryStore _store;
    private readonly TelemetryIngest _ingest;
    private readonly VehicleId _id;

    private double _longitudeDegrees = -86.5861;

    public TestVehicle(IServiceProvider services, string id = "TEST-01")
    {
        _store = services.GetRequiredService<ITelemetryStore>();
        _ingest = services.GetRequiredService<TelemetryIngest>();
        _id = VehicleId.From(id);
    }

    /// <summary>The id this vehicle reports under, as it will appear on the wire.</summary>
    public string Id => _id.Value;

    /// <summary>
    /// Reports one frame, a little further east than the last, and returns once the store has it.
    /// </summary>
    public void Report()
    {
        //  The clock is read here, before the telemetry is built, because that is what the
        //  boundary is for: the receipt stamps arrival and the work after it is measured rather
        //  than folded invisibly into the frame's recorded age.
        TelemetryReceipt receipt = _ingest.BeginReceive();

        VehicleTelemetry telemetry = VehicleTelemetry.Create(
            _id,
            latitudeDegrees: 34.7304,
            longitudeDegrees: _longitudeDegrees,
            Altitude.FromMeters(300, AltitudeReference.Msl),
            groundSpeedMetersPerSecond: 21.5,
            headingDegrees: 90,
            batteryPercent: 97.5,
            LinkStatus.Healthy);

        _longitudeDegrees += LongitudeStepDegrees;

        _store.Write(receipt.Complete(telemetry));
    }
}
