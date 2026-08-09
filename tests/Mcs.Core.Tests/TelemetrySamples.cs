using System.Globalization;

namespace Mcs.Core.Tests;

/// <summary>
/// Builds a valid <see cref="VehicleTelemetry"/> that tests vary one field of at a time.
/// </summary>
/// <remarks>
/// <see cref="VehicleTelemetry.Create"/> takes eight arguments, seven of which are irrelevant to
/// any given validation case. Spelling all eight out at each call site would bury the one value
/// under test among seven that are only there to be valid -- so the defaults live here and a test
/// names, by keyword, exactly the field it is exercising.
/// <para>
/// The values are the ones from the type's own XML example, so what the tests assert against and
/// what the documentation claims stay the same thing.
/// </para>
/// </remarks>
internal static class TelemetrySamples
{
    /// <summary>The sample vehicle, as a raw string for tests that need to build the id themselves.</summary>
    public const string Id = "UAV-01";

    /// <summary>
    /// Creates a valid report, overriding any subset of its fields.
    /// </summary>
    /// <remarks>
    /// <paramref name="id"/> and <paramref name="altitude"/> are nullable only so "leave the
    /// default" is expressible; passing <c>default(VehicleId)</c> or <c>default(Altitude)</c>
    /// reaches <see cref="VehicleTelemetry.Create"/> untouched, which is what the
    /// uninitialised-struct tests depend on. <paramref name="batteryPercent"/> is different: it is
    /// genuinely nullable in the domain, so a <see langword="null"/> passed here means "not
    /// reported" and is forwarded as-is.
    /// </remarks>
    public static VehicleTelemetry Telemetry(
        VehicleId? id = null,
        double latitudeDegrees = 51.5074,
        double longitudeDegrees = -0.1278,
        Altitude? altitude = null,
        double groundSpeedMetersPerSecond = 14.2,
        double headingDegrees = 12.5,
        double? batteryPercent = 87.0,
        LinkStatus linkStatus = LinkStatus.Healthy) =>
        VehicleTelemetry.Create(
            id ?? VehicleId.From(Id),
            latitudeDegrees,
            longitudeDegrees,
            altitude ?? Altitude.FromMeters(120, AltitudeReference.Agl),
            groundSpeedMetersPerSecond,
            headingDegrees,
            batteryPercent,
            linkStatus);

    /// <summary>
    /// The interval <see cref="Frames"/> advances the clock by between consecutive frames.
    /// </summary>
    /// <remarks>
    /// 100 ms is the 10 Hz ceiling the plan sizes everything else against, so a run of
    /// <see cref="ITelemetryStore.HistoryDepthPerVehicle"/> frames spans exactly the one minute
    /// of history that constant's comment claims. Choosing anything else would make the store's
    /// sizing arithmetic and the tests' arithmetic two different things.
    /// </remarks>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The <paramref name="number"/>th sample vehicle -- <c>Vehicle(1)</c> is <see cref="Id"/>.
    /// </summary>
    /// <remarks>
    /// Store tests need more distinct vehicles than a single sample id can provide, and the
    /// capacity cases need one more than <see cref="ITelemetryStore.MaxVehicles"/>. Formatted
    /// invariantly so a test running under <see cref="CultureScope"/> mints the same ids as one
    /// that is not.
    /// </remarks>
    public static VehicleId Vehicle(int number) =>
        VehicleId.From(string.Create(CultureInfo.InvariantCulture, $"UAV-{number:00}"));

    /// <summary>
    /// Mints a frame the only way the domain permits: through the real ingest boundary.
    /// </summary>
    /// <remarks>
    /// There is no <c>InternalsVisibleTo</c> in this solution and
    /// <see cref="TelemetryFrame.Create"/> is internal, so a test cannot fabricate a frame -- it
    /// has to go through <see cref="TelemetryIngest.BeginReceive"/> and
    /// <see cref="TelemetryReceipt.Complete"/> like the adapters do. That is a feature: every
    /// test built on this helper exercises MCS-005 end to end rather than assuming it.
    /// <para>
    /// Takes the <see cref="TimeProvider"/> rather than an arrival instant, which is the one way
    /// this differs from the private helper it replaced in <c>TelemetryFrameTests</c>. Store tests
    /// need a <i>run</i> of frames from one advancing clock, so that the stamps they are ordered
    /// and evicted by are the real ones; a helper that constructs a fresh clock per call can only
    /// ever mint frames that all claim to have arrived at the same instant.
    /// </para>
    /// </remarks>
    /// <param name="clock">The station clock. Read once, at <see cref="TelemetryIngest.BeginReceive"/>.</param>
    /// <param name="telemetry">The report to stamp, or <see langword="null"/> for <see cref="Telemetry()"/>.</param>
    /// <returns>A frame stamped with <paramref name="clock"/>'s current reading.</returns>
    public static TelemetryFrame Frame(TimeProvider clock, VehicleTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new TelemetryIngest(clock)
            .BeginReceive()
            .Complete(telemetry ?? Telemetry());
    }

    /// <summary>
    /// Mints <paramref name="count"/> frames for one vehicle, <b>advancing <paramref name="clock"/></b>
    /// by <see cref="FrameInterval"/> after each, so every frame carries a distinct and increasing
    /// <see cref="TelemetryFrame.ReceivedAtUtc"/>.
    /// </summary>
    /// <remarks>
    /// The ring cases need <see cref="ITelemetryStore.HistoryDepthPerVehicle"/> frames and one
    /// more, which is not a quantity anyone writes out by hand.
    /// <para>
    /// <b>This advances the clock, and the plural name is the warning.</b> A sample factory with a
    /// side effect is a surprise worth stating twice: after the call, <paramref name="clock"/>
    /// sits one interval past the last frame, so a second call continues the run rather than
    /// colliding with the end of the first.
    /// </para>
    /// <para>
    /// <see cref="FakeClock"/> rather than <see cref="TimeProvider"/>, because advancing is the
    /// point. Note that <see cref="FakeClock.Advance"/> is not thread-safe -- concurrency tests
    /// must mint their frames here, on one thread, and race only the writes.
    /// </para>
    /// <para>
    /// One <see cref="VehicleTelemetry"/> instance is shared by every frame in the run. The frames
    /// differ by receipt time, which is what the store orders and evicts by; re-validating eight
    /// identical fields six hundred times would only slow the suite down.
    /// </para>
    /// </remarks>
    /// <param name="clock">The clock to stamp from and advance.</param>
    /// <param name="count">How many frames to mint.</param>
    /// <param name="id">The vehicle they all report for, or <see langword="null"/> for <see cref="Id"/>.</param>
    /// <returns>The frames, oldest first.</returns>
    public static TelemetryFrame[] Frames(FakeClock clock, int count, VehicleId? id = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        VehicleTelemetry telemetry = Telemetry(id: id);
        TelemetryFrame[] frames = new TelemetryFrame[count];

        for (int i = 0; i < count; i++)
        {
            frames[i] = Frame(clock, telemetry);
            clock.Advance(FrameInterval);
        }

        return frames;
    }

    /// <summary>
    /// The exact text <see cref="object.ToString"/> produces for an unmodified
    /// <see cref="Telemetry"/>, pinned once so the frame's own formatting test can quote it.
    /// </summary>
    public const string TelemetryText =
        "VehicleTelemetry { Id = UAV-01, LatitudeDegrees = 51.5074, LongitudeDegrees = -0.1278, "
        + "Altitude = 120 m Agl, GroundSpeedMetersPerSecond = 14.2, HeadingDegrees = 12.5, "
        + "BatteryPercent = 87, LinkStatus = Healthy }";
}
