using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Mcs.Core;

/// <summary>
/// The in-process <see cref="ITelemetryStore"/>: a fixed ring per vehicle, a hard cap on vehicles,
/// and a bounded drop-oldest queue per subscriber.
/// </summary>
/// <remarks>
/// <b>Writers serialise on one gate; readers do not take it at all.</b> <see cref="Write"/> holds
/// <c>_subscriberGate</c> for its whole body -- resolve or admit, append, fan out -- so at most one
/// frame is being recorded at any moment, store-wide. The readers never touch that gate; they read
/// the dictionary lock-free and take only the per-vehicle lock of whatever they are copying out. The
/// cost therefore lands entirely on the write path, where there is headroom: 120 frames a second
/// against a critical section that is a dictionary lookup, an array store and a walk of a handful of
/// channels.
/// <para>
/// <b>Why the entire write is one critical section.</b> A write touches the ring, which decides what
/// a later seed contains, and the subscriber list, which decides who is fanned to now.
/// <see cref="Subscribe"/> holds the same gate across seed-and-register, so anything left outside is
/// a window it can land in: append outside and fan out inside delivers the frame twice, fan out
/// inside and append after delivers it never (HAZ-01). Holding both makes it exactly once whichever
/// side wins. It also closes an inversion involving no subscription race at all -- a thread preempted
/// between its own append and its own fan-out would deliver an older frame after a newer one to a
/// subscriber registered throughout.
/// </para>
/// <para>
/// <b>No separate admission lock, and no eviction contention.</b> Count-then-<c>TryAdd</c> is racy
/// alone but atomic here, since every writer holds the gate across the pair. Each
/// <see cref="VehicleRing"/> still owns its buffer so a 10 Hz vehicle cannot push a quieter one's
/// history out -- but its lock now buys reader-versus-writer exclusion only, the writers being
/// serialised above it.
/// </para>
/// <para>
/// <b>DESIGN NOTE -- a faster shape was rejected.</b> Subscribers in an <c>ImmutableArray</c> swapped
/// by <c>Interlocked</c> and read lock-free, with the append outside any store-wide lock. Faster, and
/// not correctable on its own, for the reasons above. The price paid instead is that writes for
/// different vehicles now contend -- affordable because the fan-out always took this gate anyway, so
/// the append costs no extra acquisition, only a slightly longer hold. Revert it if you disagree; the
/// subscription tests are written against the property, not the mechanism.
/// </para>
/// </remarks>
public sealed class InMemoryTelemetryStore : ITelemetryStore
{
    //  Concurrent for the readers' sake, not the writers': writes are already serialised on the gate
    //  below, but GetLatest and GetLatestSnapshot read this without any lock at all.
    private readonly ConcurrentDictionary<VehicleId, VehicleRing> _rings = new();

    //  The store's one write-side lock. Named for what it protects rather than everything it now
    //  orders; see the design note on the type for why a write may not straddle it.
    private readonly Lock _subscriberGate = new();

    //  Every channel is bounded and drop-oldest, so writing to one never blocks and holding the gate
    //  while fanning out is safe.
    private readonly List<Channel<TelemetryFrame>> _subscribers = [];

    /// <inheritdoc />
    public void Write(TelemetryFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        VehicleId id = frame.Telemetry.Id;

        //  Resolve-or-admit, append and fan out are one critical section, so a subscriber sees this
        //  frame exactly once -- either already in its seed, or live. See the type's remarks.
        lock (_subscriberGate)
        {
            if (_rings.TryGetValue(id, out VehicleRing? ring))
            {
                //  A known vehicle stays writable at capacity: MaxVehicles caps how many vehicles
                //  exist, not how much the admitted ones may say.
                ring.Append(frame);
            }
            else
            {
                //  Atomic only because every writer holds the gate across the pair. The throw precedes
                //  every mutation, so a rejected write reaches no subscriber and changes nothing.
                if (_rings.Count >= ITelemetryStore.MaxVehicles)
                {
                    throw new TelemetryStoreCapacityExceededException(id);
                }

                //  Populate before publishing: GetLatestSnapshot enumerates `_rings` without this
                //  gate, so a ring inserted empty is one another thread can observe with no frames in
                //  it -- which would force VehicleRing.Latest nullable purely as an artefact of
                //  insertion order.
                VehicleRing admitted = new();
                admitted.Append(frame);
                _rings[id] = admitted;
            }

            //  TryWrite on a bounded drop-oldest channel always succeeds and never blocks, which is
            //  what makes holding a store-wide lock across this loop safe. The result is ignored
            //  deliberately: false would mean the writer was completed, which Drain only does after
            //  removing the channel under this same gate.
            foreach (Channel<TelemetryFrame> subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(frame);
            }
        }
    }

    /// <inheritdoc />
    public TelemetryFrame? GetLatest(VehicleId id)
    {
        return _rings.TryGetValue(id, out VehicleRing? ring) ? ring.Latest : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<TelemetryFrame> GetLatestSnapshot()
    {
        //  Enumerating a ConcurrentDictionary is safe under a concurrent admit but is not a
        //  point-in-time snapshot -- a vehicle admitted mid-enumeration may or may not appear.
        //  Acceptable for GET /api/vehicles, and it is why Subscribe calls this under the gate: with
        //  every writer on that gate, the same code that is approximate here is exact there.
        List<TelemetryFrame> snapshot = new(_rings.Count);
        foreach (KeyValuePair<VehicleId, VehicleRing> pair in _rings)
        {
            //  Not null-checked, and that is a property of Write rather than an assumption: a ring is
            //  appended to before it is published, so a ring visible here holds at least one frame.
            snapshot.Add(pair.Value.Latest);
        }

        return snapshot;
    }

    /// <inheritdoc />
    public IReadOnlyList<TelemetryFrame> GetHistory(VehicleId id)
    {
        return _rings.TryGetValue(id, out VehicleRing? ring) ? ring.Snapshot() : [];
    }

    /// <inheritdoc />
    public bool Forget(VehicleId id)
    {
        //  Under the gate: a removal landing between Write's capacity check and its insert would
        //  leave that check already stale, and one landing mid-seed would pull a ring out from under
        //  Subscribe's enumeration. Readers are unaffected -- nothing tears, since the ring is only
        //  unpublished here, not mutated.
        lock (_subscriberGate)
        {
            return _rings.TryRemove(id, out _);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TelemetryFrame> Subscribe(CancellationToken cancellationToken)
    {
        //  Required by the contract; see ITelemetryStore.Subscribe for why. A store-owned
        //  CancellationTokenSource would only move the problem -- something still has to decide when
        //  to cancel it, and nothing here knows.
        if (!cancellationToken.CanBeCanceled)
        {
            throw new ArgumentException(
                "A telemetry subscription needs a token that can actually be cancelled. "
                + "Registration is eager, so this token is the only handle on a subscription "
                + "whose enumeration has not started, and Register on a non-cancellable token "
                + "does nothing -- the subscription could never be released. Pass a token from a "
                + "CancellationTokenSource you own, or the request-aborted token.",
                nameof(cancellationToken));
        }

        //  NOT an iterator -- the absence of `yield` is what makes registration eager, as the contract
        //  requires. Worth knowing why it survives review: a test that enumerates immediately cannot
        //  tell the two shapes apart, so nothing here would catch the regression.
        Channel<TelemetryFrame> channel = Channel.CreateBounded<TelemetryFrame>(
            new BoundedChannelOptions(ITelemetryStore.SubscriberBufferCapacity)
            {
                //  Drop-oldest is the contract's hazard decision; here it is also what makes the
                //  fan-out in Write non-blocking, and so safe to hold the gate across.
                FullMode = BoundedChannelFullMode.DropOldest,

                //  Assertions the channel may optimise against, so they have to be true. SingleReader
                //  holds because the channel is per subscription and only Drain reads it.
                SingleReader = true,
                SingleWriter = false,
            });

        //  Seed and register together, under the gate Write holds for its whole body. Neither half
        //  works alone: seed-then-register loses a frame slipping between, register-then-seed
        //  delivers the stale seed after the live frame that superseded it.
        lock (_subscriberGate)
        {
            //  Exact from in here even though approximate from outside: only Write mutates `_rings`,
            //  and Write needs this gate. No lock-order inversion -- this takes the gate then each
            //  ring's lock, the same order Write takes them.
            foreach (TelemetryFrame frame in GetLatestSnapshot())
            {
                //  Cannot fail, and cannot overflow: at most MaxVehicles (12) frames into 256 slots.
                channel.Writer.TryWrite(frame);
            }

            //  Seed first. Under this gate the reverse is equally correct, but if anyone ever narrows
            //  the lock, this order degrades into a duplicate frame and the other into a lost one.
            _subscribers.Add(channel);
        }

        //  The subscription is live as of this line, which is why cancellation cannot be left to
        //  Drain's finally: that runs only if the caller enumerates, and the SSE endpoint can fail
        //  between `Subscribe(ctx.RequestAborted)` and its first `await foreach` -- a header flush to
        //  a client that has gone, an early BadRequest, a losing Task.WhenAny branch. This
        //  registration is what makes "cancelling unregisters the subscriber" true on that path.
        //
        //  Outside the gate deliberately: an already-cancelled token runs the callback synchronously
        //  on this thread, and the callback takes the gate itself.
        CancellationTokenRegistration registration =
            cancellationToken.Register(() => Unsubscribe(channel));

        //  Boxed because each GetAsyncEnumerator call after the first produces a fresh copy of the
        //  iterator's state machine, fields and all -- a value captured by value would read zero every
        //  time. See the guard at the top of Drain.
        StrongBox<int> enumerated = new(0);

        return Drain(channel, registration, enumerated, cancellationToken);
    }

    /// <summary>
    /// Releases one subscription: out of the fan-out list first, then the channel completed.
    /// </summary>
    /// <remarks>
    /// Both endings call this -- <see cref="Drain"/> unwinding, and the cancellation callback -- and
    /// for a cancelled enumeration both run, on different threads. Safe because each step is
    /// idempotent. The order is load-bearing: removing under the gate <i>before</i> completing the
    /// writer is what lets <see cref="Write"/> treat a false from <c>TryWrite</c> as impossible, since
    /// any fan-out in flight holds the gate this acquires.
    /// </remarks>
    private void Unsubscribe(Channel<TelemetryFrame> channel)
    {
        lock (_subscriberGate)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    /// <summary>
    /// Drains one subscriber's channel, and unregisters it however the enumeration ends. Separate
    /// from <see cref="Subscribe"/> so registration is eager and only the reading is lazy.
    /// </summary>
    private async IAsyncEnumerable<TelemetryFrame> Drain(
        Channel<TelemetryFrame> channel,
        CancellationTokenRegistration registration,
        StrongBox<int> enumerated,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        //  The contract's single-use rule. Enforced here rather than in a wrapper's
        //  GetAsyncEnumerator, which would catch it one call earlier but would have to re-link
        //  Subscribe's token with WithCancellation's by hand -- the exact CreateLinkedTokenSource the
        //  comment below avoids, and the thing most likely to leak.
        if (Interlocked.Exchange(ref enumerated.Value, 1) != 0)
        {
            //  Before the try, so the rejected enumeration's finally does not tear down the first
            //  enumerator's live subscription.
            throw new InvalidOperationException(
                "This telemetry subscription has already been enumerated. ITelemetryStore.Subscribe "
                + "returns a single-use stream; call Subscribe again for a second reader.");
        }

        //  The two tokens are already combined, and not by anything written here: passing Subscribe's
        //  token to an [EnumeratorCancellation] parameter makes the generated GetAsyncEnumerator link
        //  it with any WithCancellation token and dispose the link with the enumerator. A hand-rolled
        //  CreateLinkedTokenSource would be redundant and would need disposing on every exit below.
        try
        {
            //  ReadAllAsync throws OperationCanceledException when the token fires. Deliberately not
            //  caught: swallowing it would make a cancelled stream look like one that ended normally,
            //  and the SSE endpoint could not tell "client went away" from "fleet went quiet".
            await foreach (TelemetryFrame frame in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            //  Unregister, not Dispose: Dispose blocks until a callback running on another thread
            //  returns, and that callback wants `_subscriberGate` -- harmless here, a deadlock waiting
            //  for someone to move this inside a lock. Not waiting costs nothing, the work being
            //  idempotent.
            registration.Unregister();

            //  A finally, not a tail after the loop, because the loop's normal exit is the rare case:
            //  cancellation throws, and abandoning an `await foreach` disposes mid-iteration. A
            //  subscriber left in the list on those paths leaks, with no symptom but a slow memory
            //  climb after a few thousand SSE reconnects.
            Unsubscribe(channel);
        }
    }

    /// <summary>
    /// One vehicle's fixed-size circular buffer of frames, oldest evicted first. The array is
    /// allocated once at admission and never resized, so the write path allocates nothing.
    /// </summary>
    private sealed class VehicleRing
    {
        private readonly Lock _gate = new();

        private readonly TelemetryFrame[] _frames =
            new TelemetryFrame[ITelemetryStore.HistoryDepthPerVehicle];

        //  A next-write cursor rather than a newest-frame index, so the empty ring needs no sentinel:
        //  0 is a valid starting value, whereas "index of newest" would start at -1 and be
        //  special-cased in three places.
        private int _next;

        //  Tracked rather than derived, because it cannot be derived: once the ring has wrapped,
        //  `_next` is 0 both for an empty ring and for a full one that has just come round.
        private int _count;

        /// <summary>Records a frame, evicting the oldest if the buffer is full.</summary>
        public void Append(TelemetryFrame frame)
        {
            lock (_gate)
            {
                //  Eviction is a consequence of the assignment, not a separate step: at capacity
                //  `_next` points at the oldest frame, so overwriting it in place is the eviction.
                _frames[_next] = frame;
                _next = (_next + 1) % _frames.Length;

                //  Saturate: past capacity every append evicts one and adds one.
                if (_count < _frames.Length)
                {
                    _count++;
                }
            }
        }

        /// <summary>Gets the most recently appended frame.</summary>
        /// <remarks>
        /// Non-nullable, which is a claim about <see cref="Write"/> rather than a preference: the
        /// admission path appends before publishing, so a reachable ring already holds a frame. Were
        /// it published first, this would have to be nullable -- and <see cref="GetLatest"/>'s null
        /// would then mean two different things, unknown vehicle or known-with-nothing-to-show.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The admission order above has been changed.</exception>
        public TelemetryFrame Latest
        {
            get
            {
                lock (_gate)
                {
                    //  Guarding an unreachable case on purpose: without it the empty ring returns a
                    //  default null the annotations promise is absent, surfacing later as a
                    //  NullReferenceException with a stack trace pointing at the API rather than at
                    //  the store that broke its own rule.
                    if (_count == 0)
                    {
                        throw new InvalidOperationException(
                            "A published VehicleRing must never be empty: admission appends the "
                            + "first frame before inserting the ring into `_rings`. Reaching this "
                            + "means that order was changed.");
                    }

                    //  Step back one from the cursor -- adding the length first, because C# `%` keeps
                    //  the sign of the left operand and a bare -1 % 600 is -1, not 599.
                    return _frames[(_next - 1 + _frames.Length) % _frames.Length];
                }
            }
        }

        /// <summary>
        /// Copies the retained frames out, oldest first. A fresh array escapes, never <c>_frames</c>
        /// nor a view over it: an alias would let a caller read a slot a concurrent append had
        /// overwritten -- a minute-old position mid-track, with nothing in the data marking it wrong.
        /// </summary>
        public IReadOnlyList<TelemetryFrame> Snapshot()
        {
            lock (_gate)
            {
                //  Exactly `_count` long, so a young vehicle's history is not padded with nulls the
                //  return type says cannot be there.
                TelemetryFrame[] copy = new TelemetryFrame[_count];

                //  The oldest live frame is `_count` steps behind the cursor, which covers both states
                //  the buffer can be in and is why there is no `if`: before wrapping `_next == _count`,
                //  so this is 0 and the run is the plain prefix; after wrapping it is `_next`, the slot
                //  the next append will evict, which is by definition the oldest thing present.
                int oldest = (_next - _count + _frames.Length) % _frames.Length;

                //  Two straight copies, oldest first. Min stops the first at the end of the buffer if
                //  the run wrapped, at the end of the live data if it did not -- in which case the
                //  second has length zero and does nothing.
                int firstRun = Math.Min(_count, _frames.Length - oldest);
                Array.Copy(_frames, oldest, copy, 0, firstRun);
                Array.Copy(_frames, 0, copy, firstRun, _count - firstRun);

                return copy;
            }
        }
    }
}
