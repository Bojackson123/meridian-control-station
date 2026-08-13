using System.Globalization;
using System.Text;

namespace Mcs.Core;

/// <summary>
/// A vehicle's report together with the station's record of when it arrived: the unit the store
/// holds, the API serves, and MCS-002 evaluates staleness against.
/// </summary>
/// <remarks>
/// <b>The split is by provenance.</b> Everything inside <see cref="Telemetry"/> is a claim by an
/// untrusted source; <see cref="ReceivedAtUtc"/> is the station's own observation, and the single
/// trusted time base for staleness now and deconfliction windows later. Two types means "is this
/// value trustworthy?" is answered by which object you are holding.
/// <para>
/// <b>MCS-005 -- the ingest boundary stamps <see cref="ReceivedAtUtc"/>, exactly once, at arrival.</b>
/// Structural rather than conventional: <see cref="Create"/> is <c>internal</c> and
/// <see cref="TelemetryReceipt.Complete"/> is its only caller, a receipt comes only from
/// <see cref="TelemetryIngest.BeginReceive"/> and is single-use, the properties are get-only so
/// <c>frame with { ReceivedAtUtc = ... }</c> will not compile, and <see cref="VehicleTelemetry"/> --
/// all an adapter can produce -- has no timestamp field. Outside <c>Mcs.Core</c> there is no
/// expression that yields a frame.
/// </para>
/// <para>
/// Stamping <i>late</i> is the part no type can prevent; <see cref="TelemetryReceipt.IngestDelay"/>
/// measures it instead. The rule left for a human is small: <c>BeginReceive</c> is the first
/// statement after the read.
/// </para>
/// <para>
/// <b>Two readings of the station clock, taken together at arrival.</b>
/// <see cref="ReceivedAtUtc"/> is a point on the calendar, which the API serves and a human reads;
/// <see cref="ReceivedTimestamp"/> is the monotonic partner, and it is what an <i>age</i> is
/// measured from. Both come from one <see cref="TimeProvider.GetUtcNow"/> / <see
/// cref="TimeProvider.GetTimestamp"/> pair inside <see cref="TelemetryIngest.BeginReceive"/>, so
/// neither can be substituted for the other by an adapter.
/// </para>
/// <para>
/// No <c>IsStale</c> or <c>Age</c> member, deliberately: both need a "now", and a value that reads a
/// clock is untestable and unloggable. <see cref="TelemetryCurrency"/> is where a frame and a "now"
/// meet -- in <c>Mcs.Core</c>, not in the console, because MCS-002 evaluates against the station
/// clock and a browser's clock is no more trustworthy than a vehicle's.
/// </para>
/// </remarks>
public sealed record TelemetryFrame
{
    private TelemetryFrame(
        VehicleTelemetry telemetry, DateTimeOffset receivedAtUtc, long receivedTimestamp)
    {
        Telemetry = telemetry;
        ReceivedAtUtc = receivedAtUtc;
        ReceivedTimestamp = receivedTimestamp;
    }

    /// <summary>
    /// Pairs a report with the instant it arrived. <c>internal</c> because it takes the timestamp as
    /// a parameter, which is exactly what the public surface must not do.
    /// </summary>
    internal static TelemetryFrame Create(
        VehicleTelemetry telemetry, DateTimeOffset receivedAtUtc, long receivedTimestamp) =>
        new(telemetry, receivedAtUtc, receivedTimestamp);

    /// <summary>Gets the vehicle's reported state. Every field of it is an untrusted claim.</summary>
    public VehicleTelemetry Telemetry { get; }

    /// <summary>
    /// Gets the instant the station received <see cref="Telemetry"/>. UTC by construction --
    /// <see cref="TimeProvider.GetUtcNow"/> returns a zero offset, so there is no
    /// <c>DateTimeKind</c> to have set wrongly.
    /// </summary>
    /// <remarks>
    /// Still wall time, so two frames straddling an NTP correction can carry stamps in the opposite
    /// order to their arrival. Anything needing strict rather than approximate ordering needs a
    /// sequence number; durations are not measured from this at all, see
    /// <see cref="TelemetryReceipt.Elapsed"/>.
    /// </remarks>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>
    /// Gets the monotonic reading taken alongside <see cref="ReceivedAtUtc"/>, from which
    /// <see cref="TelemetryCurrency"/> measures this frame's age.
    /// </summary>
    /// <remarks>
    /// <b>Internal, for the same reason <see cref="TelemetryReceipt"/> keeps its own private:</b> a
    /// raw tick count means nothing outside the <see cref="TimeProvider"/> that issued it, so
    /// publishing one invites a caller to subtract it from something unrelated. Everything that may
    /// legitimately read it lives in this assembly.
    /// <para>
    /// The wall-clock reading cannot do this job. It steps: an NTP correction of a minute backwards
    /// takes a minute off every vehicle's age at once, and a fleet that stopped reporting ten
    /// minutes ago renders live again -- HAZ-01 arriving from the station's own clock. A monotonic
    /// count does not step, which is why durations here are measured from it and never from a
    /// subtraction of two calendar readings.
    /// </para>
    /// </remarks>
    internal long ReceivedTimestamp { get; }

    //  Overridden solely to pin the culture: the synthesized PrintMembers uses the current culture,
    //  which would make a logged timestamp depend on the container's locale. "O" is round-trip --
    //  sortable as text, and what the JSON logs and any downstream parser expect.
    //
    //  ReceivedTimestamp is left out rather than forgotten: it is a tick count from a provider the
    //  log's reader does not have, so printing it would add a number nobody can interpret beside
    //  the one they can.
    private bool PrintMembers(StringBuilder builder)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;

        builder.Append(invariant, $"{nameof(Telemetry)} = {Telemetry}, ");
        builder.Append(invariant, $"{nameof(ReceivedAtUtc)} = {ReceivedAtUtc.ToString("O", invariant)}");

        return true;
    }
}
