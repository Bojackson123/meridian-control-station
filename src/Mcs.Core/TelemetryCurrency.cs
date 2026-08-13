using System.Globalization;

namespace Mcs.Core;

/// <summary>
/// How much the station trusts a vehicle's last report to still describe where it is (MCS-002).
/// </summary>
/// <remarks>
/// No zero member, for the same reason as <see cref="AltitudeReference"/> and
/// <see cref="LinkStatus"/>: an unassigned field reads back as 0, and 0 must never be mistakeable
/// for <see cref="Live"/>. <see cref="TelemetryCurrency"/> uses that 0 as its initialisation
/// sentinel.
/// <para>
/// <b>These are states of the <i>data</i>, not of the vehicle.</b> Nothing here decides an aircraft
/// has crashed, landed or gone home; <see cref="Lost"/> says the station has not heard from it for
/// long enough that its last known position should no longer be acted on. A vehicle can be flying
/// perfectly while its telemetry is <see cref="Lost"/>, and that is exactly the case the operator
/// must be able to see.
/// </para>
/// <para>
/// Not to be derived from <see cref="LinkStatus"/> nor vice versa. That is the vehicle's claim about
/// its own radio, made in a frame that by definition arrived; this is the station's observation of
/// silence. The last frame before a link dies almost always says <see cref="LinkStatus.Healthy"/>.
/// </para>
/// </remarks>
public enum VehicleState
{
    /// <summary>Heard from recently enough that the last report may be treated as current.</summary>
    Live = 1,

    /// <summary>
    /// Silent past <see cref="TelemetryCurrency.StaleAfter"/>. The last known position is still the
    /// best answer available, and the console must say how old it is (MCS-003).
    /// </summary>
    Stale = 2,

    /// <summary>
    /// Silent past <see cref="TelemetryCurrency.LostAfter"/>: longer than any dropout the link is
    /// expected to recover from, so the last known position is a record rather than a location.
    /// </summary>
    Lost = 3,
}

/// <summary>
/// One reading of how current a <see cref="TelemetryFrame"/> is: its age against the station clock,
/// and the state that age puts it in (MCS-002).
/// </summary>
/// <remarks>
/// <b>Derived on read, never stored.</b> Nothing in <see cref="ITelemetryStore"/> or
/// <see cref="TelemetryFrame"/> holds one of these; it is computed at the moment something asks and
/// thrown away. A stored state is a second copy of the truth, and the only thing that could keep it
/// in sync with the clock is a timer -- which can itself stall, leaving a vehicle marked
/// <see cref="VehicleState.Live"/> because a flag was never flipped. Deriving it means that code
/// path does not exist. It is also why this is a value and not a service: there is no instance to
/// forget to update.
/// <para>
/// <b>The age is monotonic, not a subtraction of two calendar readings.</b> It comes from
/// <see cref="TimeProvider.GetElapsedTime(long, long)"/> over
/// <c>TelemetryFrame.ReceivedTimestamp</c>. Wall time steps: an NTP correction of a minute backwards
/// would take a minute off every vehicle's age at once and render a fleet that stopped reporting ten
/// minutes ago as live again. That is HAZ-01 arriving from the station's own clock, in the one
/// component built to prevent it.
/// </para>
/// <para>
/// <b>The station clock only.</b> Neither the vehicle's clock nor the browser's participates. The
/// vehicle's cannot: <see cref="VehicleTelemetry"/> has no time field for one to be read from, which
/// is the whole reason it and <see cref="TelemetryFrame"/> are separate types. The browser's must
/// not: a machine thirty seconds out would render a live aircraft as lost or, far worse, a lost one
/// as live -- so the API sends this rather than the ingredients for it.
/// </para>
/// <para>
/// <b>Thresholds, not a policy object.</b> They are constants rather than configuration on purpose.
/// One sourced number is defensible; a settings surface invites a deployment where the mitigation
/// for the worst hazard in the system is effectively switched off, and "which station was that
/// configured on?" is not a question an operator should have to ask about a marker's colour.
/// </para>
///
/// <b>Example:</b>
/// <code>
/// long now = clock.GetTimestamp();                                  // read once for the whole fleet
/// foreach (TelemetryFrame frame in store.GetLatestSnapshot())
/// {
///     TelemetryCurrency currency = TelemetryCurrency.Of(frame, clock, now);
///     // currency.State == VehicleState.Stale, currency.Age == 00:00:07.4
/// }
/// </code>
/// </remarks>
public readonly record struct TelemetryCurrency
{
    /// <summary>
    /// How long a vehicle may be silent before its telemetry is <see cref="VehicleState.Stale"/>.
    /// </summary>
    /// <remarks>
    /// <b>MCS-002, and its number is the requirement's:</b> three times the slowest configured
    /// telemetry period (1 Hz), which is what distinguishes network jitter from link loss. A vehicle
    /// reporting at 1 Hz has to miss three consecutive reports to reach this, and the two or three
    /// datagrams a busy link drops in a row do not.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a vehicle may be silent before its telemetry is <see cref="VehicleState.Lost"/>.
    /// </summary>
    /// <remarks>
    /// Five times <see cref="StaleAfter"/> -- fifteen consecutive missed reports at the slowest
    /// configured rate. The two thresholds answer different questions, which is why the multiple is
    /// not 1: stale asks "is this jitter?", lost asks "has the link gone?", and a dropout a radio
    /// recovers from does so in a few seconds, not fifteen.
    /// <para>
    /// It is also bounded from above by something real. The console treats forty seconds of silence
    /// on the event stream as a dead station and reopens it, so a vehicle has to reach
    /// <see cref="VehicleState.Lost"/> well inside that window -- otherwise "one aircraft has gone
    /// quiet" and "the station has stopped talking to me" become the same picture at the same
    /// moment, and they need different responses from the operator.
    /// </para>
    /// <para>
    /// <b>The multiplier is a judgement, not a measurement.</b> The construction is sourced -- a
    /// multiple of the slowest configured telemetry period, exactly as MCS-002 sources
    /// <see cref="StaleAfter"/> -- but nothing measured says five rather than four or eight. Record
    /// it that way wherever it is published, and if a link ever gets characterised properly, this is
    /// the number that changes.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan LostAfter = 5 * StaleAfter;

    private const string UninitialisedMessage =
        "TelemetryCurrency was never initialised. Do not use 'default' or parameterless "
        + "constructors; every reading comes from FromAge or Of.";

    private const string UninitialisedText = "TelemetryCurrency(uninitialised)";

    private readonly TimeSpan _age;
    private readonly VehicleState _state;

    private TelemetryCurrency(TimeSpan age)
    {
        //  Reject, never clamp. A negative age means the frame was stamped by one TimeProvider and
        //  evaluated against another, and the clamp a reasonable person would write -- to zero --
        //  reports the vehicle as Live. Silence answered with "everything is fine" is HAZ-01 exactly,
        //  so this is louder than the bug that causes it.
        if (age < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(age),
                age,
                "A frame cannot be received in the future. This means the frame was stamped by a "
                + "different TimeProvider than the one it is being evaluated against -- the station "
                + "has exactly one clock, and both readings must come from it.");
        }

        _age = age;

        //  The whole rule, in three lines, ordered longest-first so the boundaries are inclusive at
        //  the bottom: MCS-002 says stale when no frame for three seconds, so 3.000 s is stale and
        //  not the last instant of live.
        _state = age >= LostAfter ? VehicleState.Lost
            : age >= StaleAfter ? VehicleState.Stale
            : VehicleState.Live;
    }

    /// <summary>
    /// The rule on its own: what a frame of this age is, with no clock involved.
    /// </summary>
    /// <remarks>
    /// Public because the thresholds are a system-wide commitment and the console's own tests want
    /// to drive the transitions directly. Everything with a frame in hand should use
    /// <see cref="Of(TelemetryFrame, TimeProvider)"/> instead -- an age arrived at by some other
    /// arithmetic is the thing this type exists to prevent.
    /// </remarks>
    /// <param name="age">How long since the frame arrived. Must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="age"/> is negative.</exception>
    public static TelemetryCurrency FromAge(TimeSpan age) => new(age);

    /// <summary>
    /// Reads how current a frame is, now.
    /// </summary>
    /// <param name="frame">The frame to evaluate.</param>
    /// <param name="clock">
    /// The station clock -- and specifically the same <see cref="TimeProvider"/> the frame was
    /// stamped by, since it is that provider's tick count the age is measured over.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static TelemetryCurrency Of(TelemetryFrame frame, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return Of(frame, clock, clock.GetTimestamp());
    }

    /// <summary>
    /// Reads how current a frame is as of a reading already taken from
    /// <see cref="TimeProvider.GetTimestamp"/>.
    /// </summary>
    /// <remarks>
    /// For evaluating a whole fleet: taking one reading and passing it to every vehicle makes the
    /// twelve ages coherent with each other, where twelve separate reads produce a snapshot in which
    /// no two vehicles were measured at the same instant. That is invisible at this scale and
    /// deliberate anyway -- a fleet view is one answer to one question, not twelve answers to twelve.
    /// </remarks>
    /// <param name="frame">The frame to evaluate.</param>
    /// <param name="clock">The station clock, which must be the provider that issued both timestamps.</param>
    /// <param name="nowTimestamp">A reading from <paramref name="clock"/>, at or after arrival.</param>
    /// <exception cref="ArgumentNullException">Either reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="nowTimestamp"/> precedes the frame's arrival, which means the two came from
    /// different providers.
    /// </exception>
    public static TelemetryCurrency Of(TelemetryFrame frame, TimeProvider clock, long nowTimestamp)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(clock);

        return new TelemetryCurrency(clock.GetElapsedTime(frame.ReceivedTimestamp, nowTimestamp));
    }

    /// <summary>Gets how long ago the frame arrived, by the station clock.</summary>
    /// <exception cref="InvalidOperationException">The instance was never initialised.</exception>
    public TimeSpan Age => IsInitialised
        //  A default instance would read back as a zero age -- the one value that says "just
        //  arrived", which is precisely the claim an uninitialised reading must not make.
        ? _age
        : throw new InvalidOperationException(UninitialisedMessage);

    /// <summary>Gets what <see cref="Age"/> puts the vehicle's telemetry in.</summary>
    /// <exception cref="InvalidOperationException">The instance was never initialised.</exception>
    public VehicleState State => IsInitialised
        ? _state
        : throw new InvalidOperationException(UninitialisedMessage);

    //  The constructor always assigns a declared state, so a zero state can only mean "never
    //  constructed".
    private bool IsInitialised => _state != 0;

    /// <summary>
    /// Formats as "Stale after 7.4 s", invariant. Returns
    /// "TelemetryCurrency(uninitialised)" rather than throwing, for the same reason as
    /// <see cref="Altitude.ToString()"/>: the default caller is a log or a debugger, and a formatter
    /// that throws turns a diagnostic into a second fault.
    /// </summary>
    public override string ToString() =>
        IsInitialised
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{_state} after {_age.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s")
            : UninitialisedText;
}
