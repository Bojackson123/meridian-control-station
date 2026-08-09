using System.Reflection;

namespace Mcs.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="TelemetryIngest"/> and <see cref="TelemetryReceipt"/>.
/// </summary>
/// <remarks>
/// These two types exist to make <b>MCS-005</b> -- <i>"the receipt timestamp is stamped once, at
/// the ingest boundary"</i> -- structural rather than advisory. The cases below verify the four
/// claims that carry that weight:
/// <list type="bullet">
/// <item><description>
/// The stamp comes from the injected <see cref="TimeProvider"/> at <see cref="TelemetryIngest.BeginReceive"/>,
/// not from the wall clock and not from the moment the frame is built. This is the case a
/// wall-clock test cannot distinguish, which is why <see cref="FakeClock"/> exists.
/// </description></item>
/// <item><description>
/// A receipt is spent exactly once, even when raced -- otherwise one arrival could mint two
/// frames bearing the same receipt time, a replay that would look entirely ordinary in the store.
/// </description></item>
/// <item><description>
/// The lateness no type can prevent is measured rather than hidden:
/// <see cref="TelemetryReceipt.IngestDelay"/> is frozen at completion so it still reports the
/// decode cost when the pipeline gets round to logging it.
/// </description></item>
/// <item><description>
/// That measurement survives the clock being corrected underneath it. The stamp is calendar time
/// and has to be; the duration is not, and a station that runs for weeks will see NTP step the
/// calendar out from under a decode eventually.
/// </description></item>
/// </list>
/// </remarks>
public class TelemetryIngestTests
{
    /// <summary>A decode cost with no round-number coincidences, so a wrong reading is obvious.</summary>
    private static readonly TimeSpan DecodeCost = TimeSpan.FromMilliseconds(37);

    // --- Construction -------------------------------------------------------------------------

    [Fact]
    public void Constructor_NullTimeProvider_ThrowsArgumentNullException()
    {
        // Required rather than defaulted to TimeProvider.System, so no wall-clock path exists to
        // be taken by accident -- and so a DI container that fails to supply one fails here
        // rather than at the first frame.
        ArgumentNullException ex =
            Assert.Throws<ArgumentNullException>(() => new TelemetryIngest(null!));

        Assert.Equal("timeProvider", ex.ParamName);
    }

    [Fact]
    public void RecommendedIngestBudget_IsFiftyMilliseconds()
    {
        // Pinned so widening it is a deliberate act. Derived from MCS-001's one second from frame
        // receipt to the field changing on screen: store write, SSE push and browser render all
        // have to fit in that same second, so decode taking more than about 5% of it is a signal
        // that work has crept in front of the stamp.
        Assert.Equal(TimeSpan.FromMilliseconds(50), TelemetryIngest.RecommendedIngestBudget);
        Assert.True(TelemetryIngest.RecommendedIngestBudget < TimeSpan.FromSeconds(1) / 10);
    }

    // --- BeginReceive: the clock is read at arrival --------------------------------------------

    [Fact]
    public void BeginReceive_TakesTheArrivalTimeFromTheInjectedClock()
    {
        // The requirement stated directly: the receipt timestamp comes from the injected
        // TimeProvider, not from whatever the machine's clock happened to read. An assertion this
        // exact is only possible because the clock is injected.
        TelemetryReceipt receipt = new TelemetryIngest(new FakeClock()).BeginReceive();

        Assert.Equal(FakeClock.Arrival, receipt.ReceivedAtUtc);
    }

    [Fact]
    public void BeginReceive_ReadsTheClockOnEveryCall()
    {
        FakeClock clock = new();
        TelemetryIngest ingest = new(clock);

        TelemetryReceipt first = ingest.BeginReceive();
        clock.Advance(TimeSpan.FromSeconds(2));
        TelemetryReceipt second = ingest.BeginReceive();

        // Not cached and not computed once at construction: two messages arriving two seconds
        // apart must be distinguishable, since that difference is what MCS-002's staleness and,
        // later, the deconfliction windows are both derived from.
        Assert.Equal(FakeClock.Arrival, first.ReceivedAtUtc);
        Assert.Equal(FakeClock.Arrival.AddSeconds(2), second.ReceivedAtUtc);
    }

    [Fact]
    public void BeginReceive_TwoCallsAtTheSameInstant_YieldIndependentReceipts()
    {
        // Equal timestamps must not mean a shared token. Two adapters receiving inside the same
        // clock tick each get their own single-use receipt, and spending one must not spend the
        // other.
        TelemetryIngest ingest = new(new FakeClock());

        TelemetryReceipt first = ingest.BeginReceive();
        TelemetryReceipt second = ingest.BeginReceive();

        Assert.NotSame(first, second);
        first.Complete(TelemetrySamples.Telemetry());

        TelemetryFrame frame = second.Complete(TelemetrySamples.Telemetry());
        Assert.Equal(FakeClock.Arrival, frame.ReceivedAtUtc);
    }

    [Fact]
    public void BeginReceive_WithTheSystemClock_YieldsAUtcOffset()
    {
        // "UTC by construction, not by convention" -- TimeProvider.GetUtcNow returns a zero
        // offset, so there is no DateTimeKind flag here for anyone to have set wrongly. Asserted
        // against the real clock because it is the real clock's behaviour being relied upon.
        TelemetryReceipt receipt = new TelemetryIngest(TimeProvider.System).BeginReceive();

        Assert.Equal(TimeSpan.Zero, receipt.ReceivedAtUtc.Offset);
    }

    [Fact]
    public void Ingest_IsReusableAcrossManyMessages()
    {
        // A single instance is shared by every adapter -- it holds no mutable state -- so a
        // hundred receipts in a row must all be independent and correctly stamped.
        FakeClock clock = new();
        TelemetryIngest ingest = new(clock);

        for (int i = 0; i < 100; i++)
        {
            TelemetryReceipt receipt = ingest.BeginReceive();
            Assert.Equal(FakeClock.Arrival.AddSeconds(i), receipt.ReceivedAtUtc);
            Assert.Equal(FakeClock.Arrival.AddSeconds(i), receipt.Complete(TelemetrySamples.Telemetry()).ReceivedAtUtc);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    // --- Complete: the frame carries the arrival time, not the completion time -----------------

    [Fact]
    public void Complete_StampsTheFrameWithArrival_NotWithTheTimeTheDecodeFinished()
    {
        // The entire reason receipt is split into two steps. Stamping at frame construction would
        // bake the decode cost into the recorded age of the data, invisibly and on every frame.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(DecodeCost);      // the decode takes as long as it takes
        TelemetryFrame frame = receipt.Complete(TelemetrySamples.Telemetry());

        Assert.Equal(FakeClock.Arrival, frame.ReceivedAtUtc);
        Assert.NotEqual(clock.GetUtcNow(), frame.ReceivedAtUtc);
    }

    [Fact]
    public void Complete_ReturnsAFrameHoldingTheSameTelemetryInstance()
    {
        VehicleTelemetry telemetry = TelemetrySamples.Telemetry();

        TelemetryFrame frame = new TelemetryIngest(new FakeClock()).BeginReceive().Complete(telemetry);

        // Pairs the report with the instant; it does not copy, reformat, or re-validate it.
        Assert.Same(telemetry, frame.Telemetry);
    }

    [Fact]
    public void Complete_NullTelemetry_ThrowsArgumentNullException()
    {
        TelemetryReceipt receipt = new TelemetryIngest(new FakeClock()).BeginReceive();

        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => receipt.Complete(null!));

        Assert.Equal("telemetry", ex.ParamName);
    }

    [Fact]
    public void Complete_NullTelemetry_DoesNotSpendTheReceipt()
    {
        // The null check runs before the interlocked test-and-set, so a caller who passes null by
        // mistake gets one exception rather than two: the argument error, and then a spurious
        // "already completed" on the retry that would send them looking for a replay that never
        // happened.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        Assert.Throws<ArgumentNullException>(() => receipt.Complete(null!));
        Assert.Null(receipt.IngestDelay);

        TelemetryFrame frame = receipt.Complete(TelemetrySamples.Telemetry());
        Assert.Equal(FakeClock.Arrival, frame.ReceivedAtUtc);
    }

    // --- Complete: single use ------------------------------------------------------------------

    [Fact]
    public void Complete_CalledTwice_ThrowsInvalidOperationExceptionCitingMcs005()
    {
        // A receipt that could be completed twice would let one arrival mint two frames bearing
        // the same receipt time -- a replay, and one that would look entirely ordinary in the
        // store. Throwing beats returning a duplicate, because a caller holding a spent receipt
        // has a bug that silence would hide.
        TelemetryReceipt receipt = new TelemetryIngest(new FakeClock()).BeginReceive();
        receipt.Complete(TelemetrySamples.Telemetry());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => receipt.Complete(TelemetrySamples.Telemetry()));

        // The requirement id travels with the rejection, so a log line naming this exception is
        // traceable back to what it enforces without anyone consulting the source.
        Assert.Contains("MCS-005", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BeginReceive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_CalledTwiceWithDifferentTelemetry_StillThrows()
    {
        // The guard is on the receipt, not on the payload: a second, different report is exactly
        // the case where a duplicate stamp would be hardest to spot afterwards.
        TelemetryReceipt receipt = new TelemetryIngest(new FakeClock()).BeginReceive();
        receipt.Complete(TelemetrySamples.Telemetry());

        Assert.Throws<InvalidOperationException>(
            () => receipt.Complete(TelemetrySamples.Telemetry(id: VehicleId.From("UAV-02"))));
    }

    [Fact]
    public void Complete_AfterARejectedSecondCall_LeavesTheFirstFrameAndItsDelayIntact()
    {
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(DecodeCost);
        TelemetryFrame frame = receipt.Complete(TelemetrySamples.Telemetry());

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => receipt.Complete(TelemetrySamples.Telemetry()));

        // The failed call is inert: it neither restamps the frame it already produced nor
        // overwrites the delay that was recorded for it.
        Assert.Equal(FakeClock.Arrival, frame.ReceivedAtUtc);
        Assert.Equal(DecodeCost, receipt.IngestDelay!.Value);
    }

    [Fact]
    public void Complete_RacedByManyThreads_StampsExactlyOneFrame()
    {
        // The single-use flag is interlocked because the invariant it protects is a safety one
        // and worth holding even under misuse -- a receipt is meant to stay on the thread that
        // received the message. Two threads racing on a plain bool could both read false before
        // either wrote true, and both would stamp.
        const int racers = 32;
        TelemetryReceipt receipt = new TelemetryIngest(new FakeClock()).BeginReceive();
        VehicleTelemetry telemetry = TelemetrySamples.Telemetry();

        using ManualResetEventSlim gate = new(initialState: false);
        int stamped = 0;
        int rejected = 0;
        Thread[] threads = new Thread[racers];

        for (int i = 0; i < racers; i++)
        {
            // Dedicated threads rather than the thread pool: every racer must be parked on the
            // gate before any of them runs, and a pool with fewer workers than racers cannot
            // guarantee that.
            threads[i] = new Thread(() =>
            {
                gate.Wait();
                try
                {
                    receipt.Complete(telemetry);
                    Interlocked.Increment(ref stamped);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref rejected);
                }
            });

            threads[i].Start();
        }

        gate.Set();
        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.Equal(1, stamped);
        Assert.Equal(racers - 1, rejected);
    }

    [Fact]
    public void Complete_LeavesNoWindow_WhereTheReceiptIsCompleteButItsDelayIsNull()
    {
        // Claiming the receipt and recording the delay are one interlocked write on one field,
        // which is what closes this. Held as two -- flag flipped first, delay written after --
        // there was an instant between them where a thread observing completion any way other
        // than by holding the returned frame saw a completed receipt reporting no decode cost at
        // all. The losers below observe it in exactly that way: being told they lost is the
        // observation, so the delay has to be readable by then. A latency alarm reading a receipt
        // off a continuation is the real shape of this, and a null there logs as healthy.
        const int racers = 32;
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();
        VehicleTelemetry telemetry = TelemetrySamples.Telemetry();

        clock.Advance(DecodeCost);

        using ManualResetEventSlim gate = new(initialState: false);
        int unreadableAfterCompletion = 0;
        Thread[] threads = new Thread[racers];

        for (int i = 0; i < racers; i++)
        {
            threads[i] = new Thread(() =>
            {
                gate.Wait();
                try
                {
                    receipt.Complete(telemetry);
                }
                catch (InvalidOperationException)
                {
                    // The receipt is provably complete at this point -- that is what the rejection
                    // means -- so the delay that belongs with it must already be there.
                    if (receipt.IngestDelay is null)
                    {
                        Interlocked.Increment(ref unreadableAfterCompletion);
                    }
                }
            });

            threads[i].Start();
        }

        gate.Set();
        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.Equal(0, unreadableAfterCompletion);
        Assert.Equal(DecodeCost, receipt.IngestDelay!.Value);
    }

    // --- IngestDelay: the lateness no type can prevent, measured -------------------------------

    [Fact]
    public void IngestDelay_BeforeCompletion_IsNull()
    {
        // Null rather than zero, so "not completed yet" cannot be misread by the pipeline as "a
        // decode that took no time at all".
        TelemetryReceipt receipt = new TelemetryIngest(new FakeClock()).BeginReceive();

        Assert.Null(receipt.IngestDelay);
    }

    [Fact]
    public void IngestDelay_AfterCompletion_IsTheTimeSpentDecoding()
    {
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(DecodeCost);
        receipt.Complete(TelemetrySamples.Telemetry());

        Assert.NotNull(receipt.IngestDelay);
        Assert.Equal(DecodeCost, receipt.IngestDelay.Value);
    }

    [Fact]
    public void IngestDelay_ImmediateCompletion_IsZero()
    {
        TelemetryReceipt receipt = new TelemetryIngest(new FakeClock()).BeginReceive();

        receipt.Complete(TelemetrySamples.Telemetry());

        Assert.Equal(TimeSpan.Zero, receipt.IngestDelay!.Value);
    }

    [Fact]
    public void IngestDelay_IsFrozenAtCompletion_UnlikeElapsed()
    {
        // The distinction the two members exist to draw. The pipeline reads IngestDelay after the
        // store write and the SSE push, by which point Elapsed has moved on -- so a delay that
        // was not frozen would report total time in the station rather than decode cost.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(DecodeCost);
        receipt.Complete(TelemetrySamples.Telemetry());
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(DecodeCost, receipt.IngestDelay!.Value);
        Assert.Equal(DecodeCost + TimeSpan.FromMinutes(1), receipt.Elapsed);
    }

    [Fact]
    public void IngestDelay_OverBudget_IsReportedRatherThanRejected()
    {
        // "A late frame still beats a dropped one." The budget is offered for the pipeline to
        // compare against and log; Mcs.Core may not reference a logger and does not enforce it
        // here. This test is what stops the budget quietly becoming a hard limit.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(TelemetryIngest.RecommendedIngestBudget + TimeSpan.FromSeconds(5));
        TelemetryFrame frame = receipt.Complete(TelemetrySamples.Telemetry());

        Assert.Equal(FakeClock.Arrival, frame.ReceivedAtUtc);
        Assert.True(receipt.IngestDelay!.Value > TelemetryIngest.RecommendedIngestBudget);
    }

    // --- Elapsed -------------------------------------------------------------------------------

    [Fact]
    public void Elapsed_TracksTheClockBeforeCompletion()
    {
        // Read live, so a decode can check itself against a deadline mid-flight.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        Assert.Equal(TimeSpan.Zero, receipt.Elapsed);

        clock.Advance(DecodeCost);
        Assert.Equal(DecodeCost, receipt.Elapsed);

        clock.Advance(DecodeCost);
        Assert.Equal(DecodeCost + DecodeCost, receipt.Elapsed);
    }

    [Fact]
    public void Elapsed_KeepsRunningAfterCompletion()
    {
        // Documented behaviour, and the reason the two members are not interchangeable: nothing
        // freezes Elapsed, because it is a live reading every time it is asked. A pipeline that
        // logs receipt.Elapsed in the step after Complete records total time in the station, not
        // decode cost, and gets a different number on every read. IngestDelay is the frozen one.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();
        receipt.Complete(TelemetrySamples.Telemetry());

        clock.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(TimeSpan.FromSeconds(90), receipt.Elapsed);
        Assert.Equal(TimeSpan.Zero, receipt.IngestDelay!.Value);
    }

    // --- The clock may step; a duration may not ------------------------------------------------

    [Fact]
    public void Elapsed_WhenTheWallClockStepsBackwards_IsUnaffected()
    {
        // Wall time is allowed to move: an NTP correction, an operator fixing the date, a VM
        // resuming. A step of a few hundred milliseconds is larger than everything ingest
        // measures, so a duration taken by subtracting ReceivedAtUtc from "now" would come back
        // negative here -- and a negative delay passes every budget comparison in silence.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(DecodeCost);
        clock.StepWallClock(TimeSpan.FromMilliseconds(-200));

        Assert.Equal(DecodeCost, receipt.Elapsed);
        Assert.True(receipt.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public void Elapsed_WhenTheWallClockStepsForwards_IsUnaffected()
    {
        // The mirror image, and the one that cries wolf: a forward step would make every frame in
        // flight look catastrophically late and put a decode cost in the logs that never happened.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(DecodeCost);
        clock.StepWallClock(TimeSpan.FromMinutes(5));

        Assert.Equal(DecodeCost, receipt.Elapsed);
        Assert.True(receipt.Elapsed < TelemetryIngest.RecommendedIngestBudget);
    }

    [Fact]
    public void IngestDelay_WhenTheWallClockStepsDuringTheDecode_IsStillTheDecodeCost()
    {
        // The consequence that reaches the logs. IngestDelay is what the pipeline compares against
        // RecommendedIngestBudget, so if a clock correction lands mid-decode the number recorded
        // must still be the time the decode took.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(DecodeCost);
        clock.StepWallClock(TimeSpan.FromSeconds(-30));
        receipt.Complete(TelemetrySamples.Telemetry());

        Assert.Equal(DecodeCost, receipt.IngestDelay!.Value);
    }

    [Fact]
    public void ReceivedAtUtc_IsNotAffectedByTheMonotonicReading()
    {
        // The other half of the split: the frame still carries a real calendar instant, because
        // staleness and the API need one. A monotonic tick count is meaningless outside the
        // process that read it and must never reach a stamp.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(DecodeCost);
        TelemetryFrame frame = receipt.Complete(TelemetrySamples.Telemetry());

        Assert.Equal(FakeClock.Arrival, frame.ReceivedAtUtc);
        Assert.Equal(FakeClock.Arrival, receipt.ReceivedAtUtc);
    }

    // --- Structural invariants -----------------------------------------------------------------

    [Fact]
    public void Receipt_ExposesNoPublicConstructor()
    {
        // BeginReceive is the only source of a receipt, and it reads the clock as it issues one.
        // A public constructor here would put back the argument through which an arrival time
        // could be forged.
        Assert.Empty(
            typeof(TelemetryReceipt).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void Receipt_ExposesNoWayToSetItsArrivalTime()
    {
        // Get-only throughout: a receipt cannot be re-dated between arrival and completion.
        PropertyInfo[] properties =
            typeof(TelemetryReceipt).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.NotEmpty(properties);
        Assert.All(properties, p => Assert.False(
            p.SetMethod?.IsPublic ?? false, $"{p.Name} has a public setter."));
    }

    [Fact]
    public void Ingest_ExposesNoOtherRouteToAFrame()
    {
        // BeginReceive is the whole public surface. Anything else returning a frame or a receipt
        // would be a second door into the one boundary MCS-005 names.
        MethodInfo[] methods = [.. typeof(TelemetryIngest)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly)];

        Assert.Equal("BeginReceive", Assert.Single(methods).Name);
    }

    [Fact]
    public void Types_AreSealed()
    {
        // Neither is an extension point. A subclass of TelemetryIngest could override
        // BeginReceive and hand back a receipt dated to whenever it liked.
        Assert.True(typeof(TelemetryIngest).IsSealed);
        Assert.True(typeof(TelemetryReceipt).IsSealed);
    }

    [Fact]
    public void Ingest_HoldsNoMutableState()
    {
        // What makes a single instance shareable by every adapter without a lock. The receipts it
        // hands out are the mutable part, and each belongs to one thread.
        FieldInfo[] fields = typeof(TelemetryIngest)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.True(f.IsInitOnly, $"{f.Name} is not readonly."));
    }
}
