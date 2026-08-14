using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Mcs.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="InMemoryTelemetryStore"/> and the
/// <see cref="ITelemetryStore"/> contract it implements.
/// </summary>
/// <remarks>
/// The store is where MCS-001's latency budget and HAZ-01 meet: everything the operator sees comes
/// out of it, so the failures that matter here are the quiet ones -- a frame accepted and then
/// lost, a subscriber shown a position the vehicle has already left, a thirteenth vehicle admitted
/// because two threads counted at the same time. None of those look like a bug from outside. Each
/// section below pins one of them.
/// <para>
/// Every frame is minted through the real ingest path (<see cref="TelemetrySamples.Frame"/>),
/// because there is no other way -- <see cref="TelemetryFrame.Create"/> is internal and this
/// assembly has no <c>InternalsVisibleTo</c>. So each case exercises MCS-005 on the way in as
/// well.
/// </para>
/// <para>
/// <b>The concurrency cases prove little in one pass.</b> A single green run of the admission race
/// or the subscribe race is weak evidence; run them in a short loop
/// (<c>dotnet test --filter "Raced|Race"</c>) before believing them.
/// </para>
/// </remarks>
public class InMemoryTelemetryStoreTests
{
    /// <summary>How long a test waits for a stream before deciding the implementation has hung.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long <see cref="DrainAsync"/> waits for a further frame before calling the stream quiet.
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMilliseconds(100);

    // --- Sizing: the constants are commitments, so they get asserted rather than assumed ---------

    [Fact]
    public void Constants_StateThePlansCommitments()
    {
        Assert.Equal(12, ITelemetryStore.MaxVehicles);
        Assert.Equal(600, ITelemetryStore.HistoryDepthPerVehicle);
        Assert.Equal(256, ITelemetryStore.SubscriberBufferCapacity);
    }

    [Fact]
    public void HistoryDepth_IsExactlyOneMinuteAtTheIngestCeiling()
    {
        // The depth is derived rather than picked, and this is the derivation: 10 Hz for 60 s. If
        // someone widens it, this fails, and the comment justifying the number has to be revisited
        // in the same change rather than quietly becoming false.
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            TelemetrySamples.FrameInterval * ITelemetryStore.HistoryDepthPerVehicle);
    }

    // --- Writing and reading back ---------------------------------------------------------------

    [Fact]
    public void Write_RejectsNull()
    {
        InMemoryTelemetryStore store = new();

        Assert.Throws<ArgumentNullException>(() => store.Write(null!));
    }

    [Fact]
    public void GetLatest_ReturnsTheFrameThatWasWritten()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame frame = Frame(clock);

        store.Write(frame);

        Assert.Same(frame, store.GetLatest(VehicleId.From(TelemetrySamples.Id)));
    }

    [Fact]
    public void GetLatest_ReturnsTheMostRecentWrite()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame[] frames = TelemetrySamples.Frames(clock, 3);

        foreach (TelemetryFrame frame in frames)
        {
            store.Write(frame);
        }

        Assert.Same(frames[^1], store.GetLatest(VehicleId.From(TelemetrySamples.Id)));
    }

    [Fact]
    public void GetLatest_IsNullForAVehicleTheStoreHasNeverSeen()
    {
        // Not an exception: "we have never heard from this one" is an ordinary answer for the API
        // to serve, and a throw here would make GET /api/vehicles/{id} a try/catch.
        InMemoryTelemetryStore store = new();

        Assert.Null(store.GetLatest(TelemetrySamples.Vehicle(7)));
    }

    // --- History and the ring -------------------------------------------------------------------

    [Fact]
    public void GetHistory_ReturnsEveryFrameOldestFirst()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame[] frames = TelemetrySamples.Frames(clock, 5);

        foreach (TelemetryFrame frame in frames)
        {
            store.Write(frame);
        }

        Assert.Equal(frames, store.GetHistory(VehicleId.From(TelemetrySamples.Id)));
    }

    [Fact]
    public void GetHistory_IsEmptyForAVehicleTheStoreHasNeverSeen()
    {
        InMemoryTelemetryStore store = new();

        Assert.Empty(store.GetHistory(TelemetrySamples.Vehicle(7)));
    }

    [Fact]
    public void GetHistory_ReturnsACopyThatLaterWritesDoNotChange()
    {
        // A live view over a ring being written and evicted concurrently would let a caller
        // enumerate a slot that had already been overwritten -- a minute-old frame appearing in
        // the middle of a fresh track. What escapes has to be a snapshot.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame[] frames = TelemetrySamples.Frames(clock, 3);

        foreach (TelemetryFrame frame in frames)
        {
            store.Write(frame);
        }

        IReadOnlyList<TelemetryFrame> history = store.GetHistory(VehicleId.From(TelemetrySamples.Id));
        store.Write(Frame(clock));

        Assert.Equal(3, history.Count);
        Assert.Equal(frames, history);
    }

    [Fact]
    public void Ring_KeepsTheNewestFramesAndEvictsTheOldest()
    {
        const int Overflow = 5;

        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame[] frames =
            TelemetrySamples.Frames(clock, ITelemetryStore.HistoryDepthPerVehicle + Overflow);

        foreach (TelemetryFrame frame in frames)
        {
            store.Write(frame);
        }

        IReadOnlyList<TelemetryFrame> history = store.GetHistory(VehicleId.From(TelemetrySamples.Id));

        Assert.Equal(ITelemetryStore.HistoryDepthPerVehicle, history.Count);
        Assert.Same(frames[Overflow], history[0]);
        Assert.Same(frames[^1], history[^1]);

        // Not just the ends: the wrapped read has to reassemble the whole run in order. This is
        // the assertion that catches an off-by-one in the copy.
        Assert.Equal(frames.Skip(Overflow), history);
    }

    [Fact]
    public void Ring_EvictionIsIndependentPerVehicle()
    {
        // A chatty vehicle must not push a quiet one's history out. One store-wide buffer would
        // make a quiet vehicle's track a function of how talkative its neighbours are.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        VehicleId quiet = TelemetrySamples.Vehicle(2);
        VehicleId chatty = TelemetrySamples.Vehicle(1);

        TelemetryFrame[] quietFrames = TelemetrySamples.Frames(clock, 3, quiet);
        foreach (TelemetryFrame frame in quietFrames)
        {
            store.Write(frame);
        }

        TelemetryFrame[] chattyFrames =
            TelemetrySamples.Frames(clock, ITelemetryStore.HistoryDepthPerVehicle + 100, chatty);
        foreach (TelemetryFrame frame in chattyFrames)
        {
            store.Write(frame);
        }

        Assert.Equal(quietFrames, store.GetHistory(quiet));
        Assert.Equal(ITelemetryStore.HistoryDepthPerVehicle, store.GetHistory(chatty).Count);
    }

    // --- The fleet snapshot -----------------------------------------------------------------------

    [Fact]
    public void GetLatestSnapshot_IsEmptyForANewStore()
    {
        Assert.Empty(new InMemoryTelemetryStore().GetLatestSnapshot());
    }

    [Fact]
    public void GetLatestSnapshot_ReturnsOneLatestFramePerKnownVehicle()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame firstOfOne = Frame(clock, TelemetrySamples.Vehicle(1));
        TelemetryFrame onlyOfTwo = Frame(clock, TelemetrySamples.Vehicle(2));
        TelemetryFrame secondOfOne = Frame(clock, TelemetrySamples.Vehicle(1));

        store.Write(firstOfOne);
        store.Write(onlyOfTwo);
        store.Write(secondOfOne);

        IReadOnlyList<TelemetryFrame> snapshot = store.GetLatestSnapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(secondOfOne, snapshot);
        Assert.Contains(onlyOfTwo, snapshot);
        Assert.DoesNotContain(firstOfOne, snapshot);
    }

    // --- Capacity: the thirteenth vehicle ---------------------------------------------------------

    [Fact]
    public void Write_AdmitsExactlyMaxVehicles()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        foreach (TelemetryFrame frame in OneFrameEach(clock, ITelemetryStore.MaxVehicles))
        {
            store.Write(frame);
        }

        Assert.Equal(ITelemetryStore.MaxVehicles, store.GetLatestSnapshot().Count);
    }

    [Fact]
    [Verifies("MCS-010")]
    public void Write_ThrowsWhenAFurtherVehicleWouldExceedTheCap()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = Filled(clock);
        TelemetryFrame thirteenth = Frame(clock, TelemetrySamples.Vehicle(ITelemetryStore.MaxVehicles + 1));

        // Rejected loudly rather than dropped: a return value can be discarded, and a discarded
        // rejection is HAZ-01 -- the console showing a fleet the operator believes is complete.
        Assert.Throws<TelemetryStoreCapacityExceededException>(() => store.Write(thirteenth));
    }

    [Fact]
    [Verifies("MCS-010")]
    public void CapacityException_CarriesWhatTheFeedsLogLineNeeds()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = Filled(clock);
        VehicleId rejected = TelemetrySamples.Vehicle(ITelemetryStore.MaxVehicles + 1);

        TelemetryStoreCapacityExceededException thrown =
            Assert.Throws<TelemetryStoreCapacityExceededException>(
                () => store.Write(Frame(clock, rejected)));

        // Structured, so the feed can log which vehicle was turned away without parsing Message.
        Assert.Equal(rejected, thrown.RejectedId);
        Assert.Equal(ITelemetryStore.MaxVehicles, thrown.MaxVehicles);
        Assert.Contains(rejected.Value, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_LeavesTheAdmittedVehiclesUntouchedWhenItRejects()
    {
        // A rejected write must be a no-op, not a partial one. Admitting nothing but having
        // already evicted, reordered or half-registered something would be worse than either
        // accepting or refusing outright.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame[] admitted = OneFrameEach(clock, ITelemetryStore.MaxVehicles);

        foreach (TelemetryFrame frame in admitted)
        {
            store.Write(frame);
        }

        VehicleId rejected = TelemetrySamples.Vehicle(ITelemetryStore.MaxVehicles + 1);
        Assert.Throws<TelemetryStoreCapacityExceededException>(() => store.Write(Frame(clock, rejected)));

        Assert.Equal(ITelemetryStore.MaxVehicles, store.GetLatestSnapshot().Count);
        for (int i = 0; i < admitted.Length; i++)
        {
            Assert.Same(admitted[i], store.GetLatest(TelemetrySamples.Vehicle(i + 1)));
        }

        Assert.Null(store.GetLatest(rejected));
        Assert.Empty(store.GetHistory(rejected));
    }

    [Fact]
    public void Write_StillAcceptsAKnownVehicleWhileTheStoreIsFull()
    {
        // The cap limits how many vehicles exist, not how often the ones that do may report.
        // Getting this wrong freezes the whole display the moment the twelfth vehicle appears.
        FakeClock clock = new();
        InMemoryTelemetryStore store = Filled(clock);
        VehicleId known = TelemetrySamples.Vehicle(1);
        TelemetryFrame update = Frame(clock, known);

        store.Write(update);

        Assert.Same(update, store.GetLatest(known));
        Assert.Equal(ITelemetryStore.MaxVehicles, store.GetLatestSnapshot().Count);
    }

    [Fact]
    public void Write_AdmitsAtMostMaxVehicles_WhenWritersRaceToRegisterNewOnes()
    {
        // Count-then-TryAdd on a ConcurrentDictionary is racy even though each operation is
        // individually safe: two threads can both see eleven and both add. Real threads rather
        // than the pool, so that all of them are genuinely runnable at the starting gun.
        const int Contenders = ITelemetryStore.MaxVehicles * 2;

        FakeClock clock = new();
        TelemetryFrame[] frames = OneFrameEach(clock, Contenders);
        InMemoryTelemetryStore store = new();

        ConcurrentBag<VehicleId> rejected = new();
        ConcurrentBag<Exception> unexpected = new();
        using ManualResetEventSlim start = new(false);

        Thread[] writers = new Thread[Contenders];
        for (int i = 0; i < Contenders; i++)
        {
            TelemetryFrame frame = frames[i];
            writers[i] = new Thread(() =>
            {
                start.Wait();

                try
                {
                    store.Write(frame);
                }
                catch (TelemetryStoreCapacityExceededException capacity)
                {
                    rejected.Add(capacity.RejectedId);
                }
                catch (Exception other)
                {
                    // Never let this escape: an unhandled exception on a bare thread takes the
                    // whole test host down and reports nothing useful.
                    unexpected.Add(other);
                }
            })
            {
                IsBackground = true,
                Name = $"race-writer-{i}",
            };

            writers[i].Start();
        }

        start.Set();
        foreach (Thread writer in writers)
        {
            Assert.True(writer.Join(ReadTimeout), $"{writer.Name} did not finish.");
        }

        Assert.Empty(unexpected);
        Assert.Equal(ITelemetryStore.MaxVehicles, store.GetLatestSnapshot().Count);
        Assert.Equal(Contenders - ITelemetryStore.MaxVehicles, rejected.Count);
        Assert.Equal(rejected.Count, rejected.Distinct().Count());
    }

    // --- Forgetting a vehicle: the way back from a full roster --------------------------------------

    [Fact]
    public void Forget_DropsTheVehicleAndEverythingKnownAboutIt()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        VehicleId id = VehicleId.From(TelemetrySamples.Id);

        foreach (TelemetryFrame frame in TelemetrySamples.Frames(clock, 3))
        {
            store.Write(frame);
        }

        Assert.True(store.Forget(id));

        Assert.Null(store.GetLatest(id));
        Assert.Empty(store.GetHistory(id));
        Assert.Empty(store.GetLatestSnapshot());
    }

    [Fact]
    public void Forget_IsFalseForAVehicleTheStoreHasNeverSeen()
    {
        // Not an exception. An operator clearing a track that a concurrent write had already
        // aged out, or two clicks on the same button, is an ordinary sequence and not a fault.
        InMemoryTelemetryStore store = new();

        Assert.False(store.Forget(TelemetrySamples.Vehicle(7)));
    }

    [Fact]
    public void Forget_FreesTheSlotSoAFurtherVehicleCanBeAdmitted()
    {
        // The whole reason the method exists. Admission is otherwise permanent, so a roster filled
        // by a feed inventing ids refuses every genuine vehicle until the process restarts -- the
        // fleet view unrecoverable while memory stays perfectly bounded.
        FakeClock clock = new();
        InMemoryTelemetryStore store = Filled(clock);
        VehicleId ghost = TelemetrySamples.Vehicle(1);
        TelemetryFrame arriving = Frame(clock, TelemetrySamples.Vehicle(ITelemetryStore.MaxVehicles + 1));

        Assert.Throws<TelemetryStoreCapacityExceededException>(() => store.Write(arriving));

        Assert.True(store.Forget(ghost));
        store.Write(arriving);

        Assert.Same(arriving, store.GetLatest(arriving.Telemetry.Id));
        Assert.Equal(ITelemetryStore.MaxVehicles, store.GetLatestSnapshot().Count);
        Assert.Null(store.GetLatest(ghost));
    }

    [Fact]
    public void Forget_ThenTheSameVehicleReportsAgain_AdmitsItWithFreshHistory()
    {
        // "Forget" is total rather than a pause: the returning vehicle is a new one as far as the
        // store is concerned, so nothing from before the removal can reappear in its track.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        VehicleId id = VehicleId.From(TelemetrySamples.Id);

        TelemetryFrame[] before = TelemetrySamples.Frames(clock, 3);
        foreach (TelemetryFrame frame in before)
        {
            store.Write(frame);
        }

        store.Forget(id);

        TelemetryFrame after = Frame(clock);
        store.Write(after);

        Assert.Same(after, Assert.Single(store.GetHistory(id)));
        Assert.DoesNotContain(before[0], store.GetHistory(id));
    }

    [Fact]
    public void Forget_LeavesTheOtherVehiclesUntouched()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame[] admitted = OneFrameEach(clock, ITelemetryStore.MaxVehicles);

        foreach (TelemetryFrame frame in admitted)
        {
            store.Write(frame);
        }

        store.Forget(TelemetrySamples.Vehicle(1));

        Assert.Equal(ITelemetryStore.MaxVehicles - 1, store.GetLatestSnapshot().Count);
        for (int i = 1; i < admitted.Length; i++)
        {
            Assert.Same(admitted[i], store.GetLatest(TelemetrySamples.Vehicle(i + 1)));
        }
    }

    [Fact]
    public async Task Forget_IsNotAnnouncedToExistingSubscribers_ButANewSubscriptionReseedsWithoutIt()
    {
        // Pinning a documented limitation so it stays a decision rather than a surprise. The
        // stream carries frames and a removal is not one, so a subscriber seeded before the
        // removal keeps its copy of the forgotten track until it reconnects: the console drops
        // the track on reseed, not on a push.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        VehicleId doomed = TelemetrySamples.Vehicle(1);
        VehicleId kept = TelemetrySamples.Vehicle(2);

        store.Write(Frame(clock, doomed));
        store.Write(Frame(clock, kept));

        using CancellationTokenSource cts = new();
        IAsyncEnumerable<TelemetryFrame> existing = store.Subscribe(cts.Token);

        store.Forget(doomed);

        // The existing subscription is untouched -- still live, still fed, and nothing was sent to
        // retract what it was seeded with.
        TelemetryFrame update = Frame(clock, kept);
        store.Write(update);

        List<TelemetryFrame> received = await ReadAsync(existing, 3);
        Assert.Same(update, received[^1]);

        // A subscription taken after the removal never hears of the vehicle at all.
        using CancellationTokenSource fresh = new();
        List<TelemetryFrame> reseeded = await ReadAsync(store.Subscribe(fresh.Token), 1);

        Assert.Same(update, Assert.Single(reseeded));
    }

    [Fact]
    [Verifies("MCS-010")]
    public void Forget_RacedWithWritersAdmittingNewVehicles_NeverExceedsTheCap()
    {
        // Removal is the second mutation of the vehicle table, so it has to be ordered against
        // admission the way admission is ordered against itself. A removal landing between a
        // writer's capacity check and its insert leaves that check made against a count that no
        // longer holds -- which is how a thirteenth vehicle gets in.
        const int Contenders = ITelemetryStore.MaxVehicles * 2;

        FakeClock clock = new();
        TelemetryFrame[] frames = OneFrameEach(clock, Contenders);
        InMemoryTelemetryStore store = new();

        ConcurrentBag<Exception> unexpected = new();
        using ManualResetEventSlim start = new(false);

        // One forgetter against every writer, all cycling over the same id space, so removals and
        // admissions interleave rather than merely running at the same time.
        Thread[] threads = new Thread[Contenders + 1];
        for (int i = 0; i < Contenders; i++)
        {
            TelemetryFrame frame = frames[i];
            threads[i] = new Thread(() =>
            {
                start.Wait();

                try
                {
                    store.Write(frame);
                }
                catch (TelemetryStoreCapacityExceededException)
                {
                    // Expected for most of them; the cap is the point.
                }
                catch (Exception other)
                {
                    unexpected.Add(other);
                }
            })
            {
                IsBackground = true,
                Name = $"forget-race-writer-{i}",
            };

            threads[i].Start();
        }

        threads[Contenders] = new Thread(() =>
        {
            start.Wait();

            try
            {
                for (int round = 0; round < Contenders; round++)
                {
                    store.Forget(TelemetrySamples.Vehicle((round % Contenders) + 1));
                }
            }
            catch (Exception other)
            {
                unexpected.Add(other);
            }
        })
        {
            IsBackground = true,
            Name = "forget-race-forgetter",
        };

        threads[Contenders].Start();

        start.Set();
        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(ReadTimeout), $"{thread.Name} did not finish.");
        }

        Assert.Empty(unexpected);

        // How many survive depends on how the race fell out; that it is never more than the cap
        // does not.
        Assert.InRange(store.GetLatestSnapshot().Count, 0, ITelemetryStore.MaxVehicles);
    }

    // --- Subscription -----------------------------------------------------------------------------

    [Fact]
    public void Subscribe_IsNotAnIterator()
    {
        // An `async IAsyncEnumerable` body does not start until the consumer's first
        // MoveNextAsync, so a Subscribe written that way registers nothing at the moment it
        // returns and silently loses every frame written before enumeration begins. The compiler
        // marks such a method; assert it did not have to.
        MethodInfo subscribe = typeof(InMemoryTelemetryStore)
            .GetMethod(nameof(InMemoryTelemetryStore.Subscribe))!;

        Assert.Null(subscribe.GetCustomAttribute<AsyncIteratorStateMachineAttribute>());
    }

    [Fact]
    public void Subscribe_RejectsATokenThatCannotBeCancelled()
    {
        // Eager registration makes this token the only handle on a subscription whose enumeration
        // has not started, and Register on a non-cancellable token is a no-op -- so
        // `Subscribe(default)` would hand back a stream no cancellation can ever release.
        // Accepting it is the leak; the parameter is optional in syntax only.
        InMemoryTelemetryStore store = new();

        Assert.Throws<ArgumentException>(() => store.Subscribe(default));
        Assert.Throws<ArgumentException>(() => store.Subscribe(CancellationToken.None));
    }

    [Fact]
    public void Subscribe_WhenItRejectsTheToken_RegistersNothing()
    {
        // The guard has to run before the channel is created and added, or the rejected call
        // leaves behind exactly the subscription it was refusing to create -- and one that
        // nothing holds a reference to, so it could never be released even deliberately.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        Assert.Throws<ArgumentException>(() => store.Subscribe(default));

        Assert.Equal(0, SubscriberCount(store));

        // And the store is still usable: a refusal is not allowed to disturb the write path.
        TelemetryFrame frame = Frame(clock);
        store.Write(frame);

        Assert.Same(frame, store.GetLatest(VehicleId.From(TelemetrySamples.Id)));
    }

    [Fact]
    public async Task Subscribe_SeedsWithTheCurrentSnapshot()
    {
        // Without the seed, the natural consumer implementation -- snapshot, then subscribe --
        // drops anything landing between the two calls, and a vehicle that has stopped
        // transmitting never appears in a late-joining client's view at all.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();
        TelemetryFrame one = Frame(clock, TelemetrySamples.Vehicle(1));
        TelemetryFrame two = Frame(clock, TelemetrySamples.Vehicle(2));

        store.Write(one);
        store.Write(two);

        using CancellationTokenSource cts = new();
        List<TelemetryFrame> seed = await ReadAsync(store.Subscribe(cts.Token), 2);

        // Seed order is unspecified by the contract, so compare after ordering.
        Assert.Equal(new[] { one, two }, seed.OrderBy(frame => frame.ReceivedAtUtc));
    }

    [Fact]
    public async Task Subscribe_DeliversFramesWrittenAfterItReturns()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        IAsyncEnumerable<TelemetryFrame> stream = store.Subscribe(cts.Token);

        TelemetryFrame frame = Frame(clock);
        store.Write(frame);

        Assert.Same(frame, Assert.Single(await ReadAsync(stream, 1)));
    }

    [Fact]
    public async Task Subscribe_BuffersFramesWrittenBeforeEnumerationStarts()
    {
        // The eager-registration case, stated on its own. If Subscribe only registers on first
        // MoveNextAsync, all three of these are gone and the test that reads immediately after
        // subscribing would never have noticed.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        IAsyncEnumerable<TelemetryFrame> stream = store.Subscribe(cts.Token);

        TelemetryFrame[] frames = TelemetrySamples.Frames(clock, 3);
        foreach (TelemetryFrame frame in frames)
        {
            store.Write(frame);
        }

        Assert.Equal(frames, await ReadAsync(stream, 3));
    }

    [Fact]
    public async Task Subscribe_DeliversToEverySubscriber()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        IAsyncEnumerable<TelemetryFrame> first = store.Subscribe(cts.Token);
        IAsyncEnumerable<TelemetryFrame> second = store.Subscribe(cts.Token);

        TelemetryFrame frame = Frame(clock);
        store.Write(frame);

        Assert.Same(frame, Assert.Single(await ReadAsync(first, 1)));
        Assert.Same(frame, Assert.Single(await ReadAsync(second, 1)));
    }

    [Fact]
    [Verifies("MCS-010")]
    public async Task Subscriber_ThatFallsBehind_LosesTheOldestFramesAndKeepsTheNewest()
    {
        // Drop-oldest, and the direction is the whole point. This is a state stream, not an event
        // log: when a stalled browser resumes, the operator needs to know where the vehicle is,
        // not to replay where it was. Dropping the newest instead would leave the subscriber
        // permanently behind reality while showing a smooth, complete and entirely stale picture.
        const int Overflow = 50;

        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        IAsyncEnumerable<TelemetryFrame> stream = store.Subscribe(cts.Token);

        TelemetryFrame[] frames =
            TelemetrySamples.Frames(clock, ITelemetryStore.SubscriberBufferCapacity + Overflow);
        foreach (TelemetryFrame frame in frames)
        {
            store.Write(frame);
        }

        List<TelemetryFrame> received =
            await ReadAsync(stream, ITelemetryStore.SubscriberBufferCapacity);

        Assert.Equal(ITelemetryStore.SubscriberBufferCapacity, received.Count);
        Assert.Same(frames[Overflow], received[0]);
        Assert.Same(frames[^1], received[^1]);
    }

    [Fact]
    public async Task Write_DoesNotBlockOnASubscriberThatNeverReads()
    {
        // The property the API would otherwise discover the hard way: one wedged HTTP client must
        // not be able to slow the ingest thread down, let alone stop it.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        _ = store.Subscribe(cts.Token);

        TelemetryFrame[] frames =
            TelemetrySamples.Frames(clock, ITelemetryStore.SubscriberBufferCapacity * 20);

        await Task.Run(() =>
        {
            foreach (TelemetryFrame frame in frames)
            {
                store.Write(frame);
            }
        }).WaitAsync(ReadTimeout);

        Assert.Same(frames[^1], store.GetLatest(VehicleId.From(TelemetrySamples.Id)));
    }

    [Fact]
    public async Task Cancelling_EndsTheEnumeration()
    {
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        Task<List<TelemetryFrame>> reader = ReadCoreAsync(store.Subscribe(cts.Token), int.MaxValue);

        store.Write(Frame(clock));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.WaitAsync(ReadTimeout));
    }

    [Fact]
    public async Task Cancelling_UnregistersTheSubscriber()
    {
        // A subscription not released on the cancellation path is a leak that shows up only as a
        // slow memory climb after a few thousand SSE reconnects -- which is to say, in production.
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        Task<List<TelemetryFrame>> reader = ReadCoreAsync(store.Subscribe(cts.Token), int.MaxValue);

        Assert.Equal(1, SubscriberCount(store));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.WaitAsync(ReadTimeout));

        Assert.Equal(0, SubscriberCount(store));
    }

    [Fact]
    public async Task Cancelling_UnregistersASubscriptionThatWasNeverEnumerated()
    {
        // The gap the sibling test above leaves open, and the one that actually reaches
        // production. The SSE endpoint does `var stream = store.Subscribe(ctx.RequestAborted);`
        // and can still fail before its first await foreach -- a header flush to a client that has
        // already gone, an early return, a losing Task.WhenAny branch. An implementation that
        // releases the subscription only from the enumeration's finally passes every other test in
        // this file and leaks on every one of those, forever, at a TryWrite per frame.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        _ = store.Subscribe(cts.Token);

        Assert.Equal(1, SubscriberCount(store));

        await cts.CancelAsync();

        Assert.Equal(0, SubscriberCount(store));

        // And the store is still usable afterwards: releasing a subscription nobody read must not
        // disturb the write path it was attached to.
        TelemetryFrame frame = Frame(clock);
        store.Write(frame);

        Assert.Same(frame, store.GetLatest(VehicleId.From(TelemetrySamples.Id)));
    }

    [Fact]
    public async Task Subscribing_WithAnAlreadyCancelledToken_LeavesNoSubscription()
    {
        // The synchronous corner of the same path: Register runs the callback on this thread, so
        // the release happens inside Subscribe, unwinding a registration made moments earlier.
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        IAsyncEnumerable<TelemetryFrame> stream = store.Subscribe(cts.Token);

        Assert.Equal(0, SubscriberCount(store));

        // Still the documented ending, rather than an empty stream: a cancelled subscription must
        // not look like a fleet that has gone quiet.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadCoreAsync(stream, 1).WaitAsync(ReadTimeout));
    }

    [Fact]
    public async Task Enumerating_TheSameSubscriptionTwice_Throws()
    {
        // Single-use, and it has to fail loudly. Eager registration means the returned stream *is*
        // the subscription, so a second enumeration cannot mean "subscribe again" -- and by the
        // time it happens the first enumerator's finally has completed the channel, so left
        // unguarded it ends immediately and silently. A caller reconnecting in a loop would spin on
        // a dead stream, receiving nothing and being told nothing.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        IAsyncEnumerable<TelemetryFrame> stream = store.Subscribe(cts.Token);
        store.Write(Frame(clock));

        await ReadAsync(stream, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadCoreAsync(stream, 1).WaitAsync(ReadTimeout));
    }

    [Fact]
    public async Task Enumerating_ASubscriptionTwice_DoesNotDisturbTheFirstReader()
    {
        // The rejected enumeration must not run the teardown: unregistering from it would tear
        // down the live subscription the first reader is still using.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        IAsyncEnumerable<TelemetryFrame> stream = store.Subscribe(cts.Token);

        IAsyncEnumerator<TelemetryFrame> first = stream.GetAsyncEnumerator(cts.Token);
        await using (first.ConfigureAwait(false))
        {
            store.Write(Frame(clock));
            Assert.True(await first.MoveNextAsync().AsTask().WaitAsync(ReadTimeout));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ReadCoreAsync(stream, 1).WaitAsync(ReadTimeout));

            Assert.Equal(1, SubscriberCount(store));

            TelemetryFrame next = Frame(clock);
            store.Write(next);

            Assert.True(await first.MoveNextAsync().AsTask().WaitAsync(ReadTimeout));
            Assert.Same(next, first.Current);
        }

        Assert.Equal(0, SubscriberCount(store));
    }

    [Fact]
    public async Task AbandoningTheEnumeration_UnregistersTheSubscriber()
    {
        // The other way out: breaking out of an await foreach disposes the enumerator, which must
        // release the subscription just as cancellation does.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        using CancellationTokenSource cts = new();
        IAsyncEnumerable<TelemetryFrame> stream = store.Subscribe(cts.Token);
        store.Write(Frame(clock));

        await ReadAsync(stream, 1);

        Assert.Equal(0, SubscriberCount(store));
    }

    [Fact]
    public async Task Subscribe_SeesTheNewestStateAndNeverGoesBackwards_WhenRacedWithWrites()
    {
        // The case the seeding decision exists for, and the one a single-threaded test cannot
        // reach. Two ways to get it wrong, both HAZ-01:
        //
        //   seed then register -> a frame landing between the two is never delivered, so the
        //                         subscriber's newest state is older than the store's;
        //   register then seed -> the stale seed frame arrives after the live one that superseded
        //                         it, so the console paints a position the vehicle has left.
        //
        // Asserting both properties, rather than the mechanism, leaves the implementation free.
        const int Attempts = 15;
        const int WritesPerAttempt = 40;

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            FakeClock clock = new();
            InMemoryTelemetryStore store = new();
            TelemetryFrame[] frames = TelemetrySamples.Frames(clock, WritesPerAttempt);

            using ManualResetEventSlim writing = new(false);
            Task writer = Task.Run(() =>
            {
                writing.Set();
                foreach (TelemetryFrame frame in frames)
                {
                    store.Write(frame);
                }
            });

            writing.Wait();

            using CancellationTokenSource cts = new();
            IAsyncEnumerable<TelemetryFrame> stream = store.Subscribe(cts.Token);

            await writer;

            List<TelemetryFrame> observed = await DrainAsync(stream, cts).WaitAsync(ReadTimeout);

            Assert.NotEmpty(observed);

            for (int i = 1; i < observed.Count; i++)
            {
                Assert.True(
                    observed[i].ReceivedAtUtc >= observed[i - 1].ReceivedAtUtc,
                    $"Attempt {attempt}: frame {i} ({observed[i].ReceivedAtUtc:O}) is older than "
                    + $"the one before it ({observed[i - 1].ReceivedAtUtc:O}).");
            }

            Assert.Equal(frames[^1].ReceivedAtUtc, observed[^1].ReceivedAtUtc);
        }
    }

    // --- Helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// Mints one frame and advances <paramref name="clock"/>, so consecutive calls never produce
    /// two frames claiming the same arrival instant.
    /// </summary>
    private static TelemetryFrame Frame(FakeClock clock, VehicleId? id = null)
    {
        TelemetryFrame frame = TelemetrySamples.Frame(clock, TelemetrySamples.Telemetry(id: id));
        clock.Advance(TelemetrySamples.FrameInterval);

        return frame;
    }

    /// <summary>One frame each for vehicles 1..<paramref name="vehicles"/>, all distinct instants.</summary>
    private static TelemetryFrame[] OneFrameEach(FakeClock clock, int vehicles)
    {
        TelemetryFrame[] frames = new TelemetryFrame[vehicles];
        for (int i = 0; i < vehicles; i++)
        {
            frames[i] = Frame(clock, TelemetrySamples.Vehicle(i + 1));
        }

        return frames;
    }

    /// <summary>A store holding exactly <see cref="ITelemetryStore.MaxVehicles"/> vehicles.</summary>
    private static InMemoryTelemetryStore Filled(FakeClock clock)
    {
        InMemoryTelemetryStore store = new();
        foreach (TelemetryFrame frame in OneFrameEach(clock, ITelemetryStore.MaxVehicles))
        {
            store.Write(frame);
        }

        return store;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> frames, failing rather than hanging if they do not
    /// arrive.
    /// </summary>
    /// <remarks>
    /// The timeout is applied with <see cref="TaskAsyncEnumerableExtensions"/>-free
    /// <c>WaitAsync</c> rather than a cancellation token, so it does not depend on the
    /// implementation honouring cancellation -- which is separately under test. Breaking out of
    /// the loop disposes the enumerator, which is also what releases the subscription.
    /// </remarks>
    private static Task<List<TelemetryFrame>> ReadAsync(
        IAsyncEnumerable<TelemetryFrame> stream, int count) =>
        ReadCoreAsync(stream, count).WaitAsync(ReadTimeout);

    private static async Task<List<TelemetryFrame>> ReadCoreAsync(
        IAsyncEnumerable<TelemetryFrame> stream, int count)
    {
        List<TelemetryFrame> received = [];
        if (count == 0)
        {
            return received;
        }

        await foreach (TelemetryFrame frame in stream)
        {
            received.Add(frame);
            if (received.Count == count)
            {
                break;
            }
        }

        return received;
    }

    /// <summary>
    /// Reads until the stream goes quiet for <see cref="IdleTimeout"/>, then cancels it and
    /// returns what arrived.
    /// </summary>
    /// <remarks>
    /// For the cases where the expected count is not known in advance because it depends on how a
    /// race fell out. Tolerates either cancellation contract: an implementation that throws and
    /// one that completes the stream gracefully both end up here with the same list.
    /// </remarks>
    private static async Task<List<TelemetryFrame>> DrainAsync(
        IAsyncEnumerable<TelemetryFrame> stream, CancellationTokenSource stop)
    {
        List<TelemetryFrame> received = [];
        stop.CancelAfter(IdleTimeout);

        try
        {
            await foreach (TelemetryFrame frame in stream)
            {
                received.Add(frame);
                stop.CancelAfter(IdleTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the idle timer is how this loop is meant to end.
        }

        return received;
    }

    /// <summary>
    /// Counts the store's live subscriptions by reflection.
    /// </summary>
    /// <remarks>
    /// Structural, and it pins the field name the skeleton ships with. If your implementation
    /// tracks subscriptions differently, retarget this helper -- but keep a test that proves they
    /// are released, because a leak here has no other symptom until the station has been up for a
    /// week.
    /// </remarks>
    private static int SubscriberCount(InMemoryTelemetryStore store)
    {
        FieldInfo field = typeof(InMemoryTelemetryStore)
            .GetField("_subscribers", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Expected a private '_subscribers' field on InMemoryTelemetryStore.");

        object subscribers = field.GetValue(store)
            ?? throw new InvalidOperationException("'_subscribers' was null.");

        return ((ICollection)subscribers).Count;
    }
}
