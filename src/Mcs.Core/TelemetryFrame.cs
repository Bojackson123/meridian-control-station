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
/// No <c>IsStale</c> or <c>Age</c> member, deliberately: both need a "now", and a value that reads a
/// clock is untestable and unloggable. Staleness belongs to the console layer, which holds the
/// <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
public sealed record TelemetryFrame
{
    private TelemetryFrame(VehicleTelemetry telemetry, DateTimeOffset receivedAtUtc)
    {
        Telemetry = telemetry;
        ReceivedAtUtc = receivedAtUtc;
    }

    /// <summary>
    /// Pairs a report with the instant it arrived. <c>internal</c> because it takes the timestamp as
    /// a parameter, which is exactly what the public surface must not do.
    /// </summary>
    internal static TelemetryFrame Create(VehicleTelemetry telemetry, DateTimeOffset receivedAtUtc) =>
        new(telemetry, receivedAtUtc);

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

    //  Overridden solely to pin the culture: the synthesized PrintMembers uses the current culture,
    //  which would make a logged timestamp depend on the container's locale. "O" is round-trip --
    //  sortable as text, and what the JSON logs and any downstream parser expect.
    private bool PrintMembers(StringBuilder builder)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;

        builder.Append(invariant, $"{nameof(Telemetry)} = {Telemetry}, ");
        builder.Append(invariant, $"{nameof(ReceivedAtUtc)} = {ReceivedAtUtc.ToString("O", invariant)}");

        return true;
    }
}
