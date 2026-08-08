using System.Globalization;
using System.Text;

namespace Mcs.Core;

/// <summary>
/// A vehicle's report together with the station's record of when it arrived: the unit the store
/// holds, the API serves, and MCS-002 evaluates staleness against.
/// </summary>
/// <remarks>
/// <b>MCS-005 — who stamps <see cref="ReceivedAtUtc"/>, and when.</b> The ingest boundary does,
/// exactly once, at the instant the message arrives. Nothing else, ever. The chain is built so
/// that this is not a convention anyone has to remember:
/// <list type="bullet">
/// <item><description>
/// <see cref="Create"/> is <c>internal</c>, and <see cref="TelemetryReceipt.Complete"/> is its
/// only caller. Outside <c>Mcs.Core</c> there is no expression that yields a frame -- not a
/// constructor, not a factory, nothing to be found by autocomplete and used in good faith.
/// </description></item>
/// <item><description>
/// A receipt comes only from <see cref="TelemetryIngest.BeginReceive"/>, which reads the clock
/// as it is issued. The timestamp is therefore taken at arrival rather than supplied by a
/// caller: there is no argument through which one could be forged.
/// </description></item>
/// <item><description>
/// A receipt is single-use, so one arrival cannot mint two frames sharing a receipt time.
/// </description></item>
/// <item><description>
/// The properties are get-only, so <c>frame with { ReceivedAtUtc = ... }</c> does not compile.
/// A frame cannot restamp itself, which is precisely the hole a constructor default would leave
/// open: an object held from an earlier second would quietly claim to be new.
/// </description></item>
/// <item><description>
/// <see cref="VehicleTelemetry"/>, which is all an adapter can produce, has no timestamp field
/// at all. Stamping early is not a discipline an adapter author can fail at; it is code they
/// cannot write.
/// </description></item>
/// </list>
/// <para>
/// What none of this can enforce is stamping <b>late</b> -- nothing in the process knows when
/// the bytes truly arrived, so a caller who does work before calling
/// <see cref="TelemetryIngest.BeginReceive"/> produces a frame that looks fresher than it was.
/// Two things narrow that as far as it goes. The receipt is issued <i>before</i> decoding rather
/// than after, so the decode cost -- the large, variable part -- is outside the gap by
/// construction. And what remains is measured: <see cref="TelemetryReceipt.IngestDelay"/> turns
/// the residue into a number the ingest pipeline compares against
/// <see cref="TelemetryIngest.RecommendedIngestBudget"/> and logs. The rule that survives for a
/// human to follow is now a small one: <c>BeginReceive</c> is the first statement after the read.
/// </para>
/// <para>
/// <b>Why a separate type from <see cref="VehicleTelemetry"/>.</b> The split is by provenance.
/// Everything inside <see cref="Telemetry"/> is a claim by an untrusted source, and MCS-002 is
/// explicit that vehicle time in particular is not to be believed. <see cref="ReceivedAtUtc"/>
/// is the station's own observation and is the single trusted time base for staleness now and
/// for deconfliction windows later. Two types means "can I trust this value?" is answered by
/// which object you are holding, rather than by remembering which of nine fields the station
/// filled in.
/// </para>
/// <para>
/// <b>There is deliberately no <c>IsStale</c> or <c>Age</c> member here</b>, and its absence is a
/// design decision rather than an omission. Both need a "now", and a value that reads a clock
/// gives a different answer each time it is asked, which makes it untestable and unloggable.
/// Staleness evaluation belongs to the console layer, which will hold the
/// <see cref="TimeProvider"/>; this type's job is to record the one fact that evaluation needs.
/// </para>
/// <para>
/// Serialisation: as with <see cref="VehicleTelemetry"/>, this does not cross the wire directly
/// -- no public constructor, no settable members. The API projects a DTO that flattens the frame
/// and its telemetry into one JSON object, with <see cref="ReceivedAtUtc"/> written in ISO-8601.
/// </para>
///
/// <b>Example:</b>
/// <code>
/// // In the receive loop -- BeginReceive first, then decode:
/// TelemetryReceipt receipt = ingest.BeginReceive();
/// VehicleTelemetry telemetry = Decode(rawMessage);
/// TelemetryFrame frame = receipt.Complete(telemetry);
///
/// store.Write(frame);
/// DateTimeOffset arrived = frame.ReceivedAtUtc;   // station clock, trusted
/// double lat = frame.Telemetry.LatitudeDegrees;   // vehicle's claim, not
///
/// // Will not compile -- a frame cannot restamp itself:
/// // frame = frame with { ReceivedAtUtc = clock.GetUtcNow() };
/// </code>
/// </remarks>
public sealed record TelemetryFrame
{
    private TelemetryFrame(VehicleTelemetry telemetry, DateTimeOffset receivedAtUtc)
    {
        Telemetry = telemetry;
        ReceivedAtUtc = receivedAtUtc;
    }

    /// <summary>
    /// Pairs a report with the instant it arrived. <c>internal</c>, and called only by
    /// <see cref="TelemetryReceipt.Complete"/>.
    /// </summary>
    /// <remarks>
    /// This one takes the timestamp as a parameter, which is exactly what the public surface must
    /// not do -- so it is not public. Visibility is what makes MCS-005 enforceable here: the
    /// receipt has already established the arrival time against the station clock and has already
    /// checked that it is spending that receipt once, so by the time control reaches this method
    /// the only remaining work is to store the pair. A caller outside <c>Mcs.Core</c> has no way
    /// to reach it and therefore no way to supply a time of its own choosing.
    /// </remarks>
    /// <param name="telemetry">The decoded report. Null-checked by the receipt.</param>
    /// <param name="receivedAtUtc">The arrival instant recorded by <see cref="TelemetryIngest.BeginReceive"/>.</param>
    /// <returns>The frame.</returns>
    internal static TelemetryFrame Create(VehicleTelemetry telemetry, DateTimeOffset receivedAtUtc) =>
        new(telemetry, receivedAtUtc);

    /// <summary>Gets the vehicle's reported state. Every field of it is an untrusted claim.</summary>
    public VehicleTelemetry Telemetry { get; }

    /// <summary>
    /// Gets the instant the station received <see cref="Telemetry"/>, from the station clock.
    /// </summary>
    /// <remarks>
    /// UTC by construction, not by convention: <see cref="TimeProvider.GetUtcNow"/> returns a
    /// <see cref="DateTimeOffset"/> with a zero offset, so there is no <c>DateTimeKind</c> flag
    /// here for anyone to have set wrongly. This is the only timestamp in the system that may be
    /// used for staleness (MCS-002) or for ordering frames against one another; a vehicle-supplied
    /// time, should an adapter ever surface one, is data to display and never a time base.
    /// <para>
    /// It is still wall time, with wall time's one weakness: the clock can be stepped, so two
    /// frames straddling an NTP correction can carry stamps in the opposite order to their
    /// arrival. Nothing here can prevent that -- a frame has to carry a real calendar instant to
    /// be of any use to staleness or to the API -- so anything that must order frames strictly,
    /// rather than approximately, needs a sequence number of its own rather than this field.
    /// Durations are a different matter and are not measured from this at all; see
    /// <see cref="TelemetryReceipt.Elapsed"/>.
    /// </para>
    /// </remarks>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>
    /// Formats the members for the compiler-generated <c>ToString</c>, in the invariant culture.
    /// </summary>
    /// <remarks>
    /// Same reason as <see cref="VehicleTelemetry"/>'s override: the synthesized version formats
    /// with the current culture, which would make a frame's logged timestamp depend on the
    /// container's locale. "O" is the round-trip format -- unambiguous, sortable as text, and
    /// what the station's JSON logs and any downstream parser expect.
    /// </remarks>
    private bool PrintMembers(StringBuilder builder)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;

        builder.Append(invariant, $"{nameof(Telemetry)} = {Telemetry}, ");
        builder.Append(invariant, $"{nameof(ReceivedAtUtc)} = {ReceivedAtUtc.ToString("O", invariant)}");

        return true;
    }
}
