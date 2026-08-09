using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Mcs.Core;

/// <summary>
/// The in-process <see cref="ITelemetryStore"/>: a fixed ring per vehicle, a hard cap on vehicles,
/// and a bounded drop-oldest queue per subscriber.
/// </summary>
/// <remarks>
/// <b>The locking strategy, in one sentence: writers serialise on one gate, readers do not take
/// it at all.</b> <see cref="Write"/> holds <c>_subscriberGate</c> for its whole body -- resolve
/// or admit the vehicle, append, fan out -- so at most one frame is being recorded at any moment,
/// store-wide. <see cref="GetLatest"/>, <see cref="GetHistory"/> and
/// <see cref="GetLatestSnapshot"/> never touch that gate; they read the dictionary lock-free and
/// take only the per-vehicle lock of whatever they are copying out. The whole cost of the design
/// therefore lands on the write path, which is the one place there is measured headroom: 120
/// frames a second against a critical section that is a dictionary lookup, an array store and a
/// walk of at most a handful of channels.
/// <para>
/// <b>Why the entire write is one critical section, rather than three cheap ones.</b> A write
/// touches two things a subscriber can observe: the ring, which determines what a later seed
/// contains, and the subscriber list, which determines who is fanned to now. <see cref="Subscribe"/>
/// takes the same gate across seed-and-register, so any part of the write left outside it is a
/// window <see cref="Subscribe"/> can land in -- and each half fails differently. Append outside
/// and fan out inside: a subscriber that registers between them receives the frame twice, once
/// from the seed and once live. Fan out inside and append after: a subscriber that registers
/// between them receives it neither way. The second is HAZ-01 outright. Holding both together
/// makes it exactly once whichever side wins the race.
/// <br/>
/// It also closes an inversion that involves no subscription race at all. A per-vehicle lock
/// orders two concurrent appends and the gate orders two concurrent fan-outs, but nothing ties
/// those two orders to each other: a thread preempted between its own append and its own fan-out
/// delivers an older frame after a newer one to a subscriber that was registered throughout. One
/// gate across the pair makes delivery order equal append order, globally.
/// </para>
/// <para>
/// <b>Why there is no separate admission lock.</b> There was one, and it is folded in here.
/// Count-then-<c>TryAdd</c> on a <see cref="ConcurrentDictionary{TKey, TValue}"/> is racy on its
/// own -- two threads can both observe eleven vehicles and both add, leaving thirteen -- but
/// every writer now holds the gate across that pair, so the check-then-act is already atomic and
/// a second lock would guard nothing. Admission is still the rare path; it simply no longer needs
/// its own mechanism to be correct.
/// </para>
/// <para>
/// <b>Why eviction is per vehicle.</b> Each <see cref="VehicleRing"/> owns its buffer and its
/// lock, so a vehicle reporting at 10 Hz never pushes a quieter vehicle's history out; a single
/// store-wide buffer would make every vehicle's retained minute a function of how chatty its
/// neighbours are. Note what that lock does and does not buy now: it no longer keeps two writers
/// off each other, because they are already serialised on the gate above it. It keeps the readers
/// off a buffer mid-append, which is the case that remains concurrent.
/// </para>
/// <para>
/// <b>DESIGN NOTE -- a faster shape was considered and rejected.</b> Subscribers in an
/// <c>ImmutableArray</c> swapped by <c>Interlocked</c> and read lock-free on every write, with the
/// append outside any store-wide lock. Faster, and not correctable on its own, for the two reasons
/// above. The price paid instead is real and worth naming: writes for different vehicles now
/// contend. It is affordable because the fan-out was always going to take this gate on every
/// write, so the append costs no extra acquisition -- only a slightly longer hold, which at 120
/// writes a second does not register. Revert it if you disagree; the subscription tests are
/// written against the property, not the mechanism.
/// </para>
/// </remarks>
public sealed class InMemoryTelemetryStore : ITelemetryStore
{
    /// <summary>
    /// Per-vehicle state. Concurrent for the readers' sake, not the writers': writes are already
    /// serialised on <see cref="_subscriberGate"/>, but <see cref="GetLatest"/> and
    /// <see cref="GetLatestSnapshot"/> read this without any lock at all. The value's own lock
    /// guards its buffer.
    /// </summary>
    private readonly ConcurrentDictionary<VehicleId, VehicleRing> _rings = new();

    /// <summary>
    /// The store's one write-side lock. Held by <see cref="Subscribe"/> across register-and-seed,
    /// and by <see cref="Write"/> across its entire body -- admission, append and fan-out
    /// together. Named for what it protects rather than everything it now orders; see the design
    /// note on the type for why the write is not allowed to straddle it.
    /// </summary>
    private readonly Lock _subscriberGate = new();

    /// <summary>
    /// The live subscriptions. Every channel is bounded and drop-oldest, so writing to one never
    /// blocks and holding <see cref="_subscriberGate"/> while fanning out is safe.
    /// </summary>
    private readonly List<Channel<TelemetryFrame>> _subscribers = [];

    /// <inheritdoc />
    public void Write(TelemetryFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        VehicleId id = frame.Telemetry.Id;

        //  Resolve-or-admit, append and fan out are one critical section, and that is the whole
        //  answer to the ordering question. The append decides what a future seed contains; the
        //  fan-out decides what an already-registered subscriber sees. Subscribe takes this same
        //  gate across seed-and-register, so as long as the two happen together, a subscriber sees
        //  this frame exactly once: either it is already in the seed (this section ran first, and
        //  the subscriber was not yet in `_subscribers` to be fanned to) or it is not (Subscribe's
        //  section ran first, and the subscriber was registered in time for the fan-out).
        //
        //  Splitting the pair breaks it, and each half breaks differently. Append outside and fan
        //  out inside, and a subscriber registering between the two gets the frame twice -- once
        //  from the seed, once live. Fan out inside and append after, and a subscriber registering
        //  between the two never sees it: too late for the fan-out, too early for the seed. The
        //  second is HAZ-01 outright.
        //
        //  It also closes an inversion that has nothing to do with Subscribe. The ring's own lock
        //  orders two concurrent appends and this gate orders two concurrent fan-outs, but nothing
        //  tied those orders together: a thread preempted between its append and its fan-out could
        //  deliver an older frame after a newer one to a subscriber that had been registered the
        //  whole time. One gate across the pair makes delivery order equal append order, globally.
        //
        //  The cost is that writes for different vehicles serialise here, which is a real
        //  departure from the per-vehicle independence claimed on the type. It is affordable
        //  because the fan-out already took this gate on every write: this is the same single
        //  uncontended acquisition, merely held across an array store as well. The ring's own lock
        //  still earns its place against the readers -- GetLatest, GetHistory and
        //  GetLatestSnapshot never take this gate.
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
                //  Admission. Count-then-add is a check-then-act across two dictionary operations
                //  and is racy on its own, but it is atomic here: every writer holds this gate for
                //  the whole sequence, so no other thread can be counting or adding concurrently.
                //  The throw precedes every mutation, so a rejected write leaves the admitted
                //  vehicles exactly as they were and reaches no subscriber.
                if (_rings.Count >= ITelemetryStore.MaxVehicles)
                {
                    throw new TelemetryStoreCapacityExceededException(id);
                }

                //  Populate before publishing, not after. GetLatestSnapshot enumerates `_rings`
                //  without taking this gate, so a ring inserted empty and appended to a moment
                //  later is one another thread can observe with no frames in it -- and that would
                //  make VehicleRing.Latest nullable for a reason that is purely an artefact of the
                //  insertion order rather than anything true about the vehicle.
                VehicleRing admitted = new();
                admitted.Append(frame);
                _rings[id] = admitted;
            }

            //  TryWrite on a bounded drop-oldest channel always succeeds and never blocks, so the
            //  fan-out cannot stall the ingest thread however wedged a subscriber is. That is the
            //  reason the channel mode was chosen, and it is what makes holding a store-wide lock
            //  across this loop safe. The result is ignored deliberately: false would mean the
            //  writer was completed, which Drain only does after removing the channel from
            //  `_subscribers` under this same gate, so it cannot be seen from in here.
            foreach (Channel<TelemetryFrame> subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(frame);
            }
        }
    }

    /// <inheritdoc />
    public TelemetryFrame? GetLatest(VehicleId id)
    {
        //  The ring is created on admission, which happens on a write. A vehicle that has never
        //  been admitted has no history, so the result is null. The ring's own lock guards its
        //  buffer, so the read is safe even if a concurrent write is mid-append.
        return _rings.TryGetValue(id, out VehicleRing? ring) ? ring.Latest : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<TelemetryFrame> GetLatestSnapshot()
    {
        //  Enumerating a ConcurrentDictionary is safe under a concurrent admit but is not a
        //  point-in-time snapshot -- a vehicle admitted mid-enumeration may or may not appear.
        //  That is acceptable for GET /api/vehicles, and it is why Subscribe calls this while
        //  holding `_subscriberGate`: with every writer on that gate, the dictionary cannot change
        //  underneath the loop, so the same code that is approximate here is exact there.
        //
        //  Each ring's own lock guards its buffer, so reading Latest is safe against a concurrent
        //  append. Count is a sizing hint only -- it may have moved by the time the list fills --
        //  which costs at worst one resize and never correctness.
        List<TelemetryFrame> snapshot = new(_rings.Count);
        foreach (KeyValuePair<VehicleId, VehicleRing> pair in _rings)
        {
            //  Not null-checked, and that is a property of Write rather than an assumption: a ring
            //  is appended to before it is published into `_rings` (see the admission path), so a
            //  ring visible here has at least one frame. VehicleRing.Latest is non-nullable to say
            //  so in the type rather than in a comment.
            snapshot.Add(pair.Value.Latest);
        }

        return snapshot;
    }

    /// <inheritdoc />
    public IReadOnlyList<TelemetryFrame> GetHistory(VehicleId id)
    {
        //  The ring is created on admission, which happens on a write. A vehicle that has never
        //  been admitted has no history, so the result is empty. The ring's own lock guards its
        //  buffer, so the read is safe even if a concurrent write is mid-append.
        return _rings.TryGetValue(id, out VehicleRing? ring) ? ring.Snapshot() : [];
    }

    /// <inheritdoc />
    public bool Forget(VehicleId id)
    {
        //  Under the gate, because this is the only mutation of `_rings` other than admission and
        //  the two must not interleave: a removal landing between Write's capacity check and its
        //  insert would leave that check already stale. It also settles Subscribe, which seeds
        //  under this same gate -- a removal cannot land mid-seed, so a new subscriber's snapshot
        //  either contains the forgotten vehicle or does not, never a ring pulled out from under
        //  the enumeration.
        lock (_subscriberGate)
        {
            //  Readers are unaffected, deliberately. They reach `_rings` without this gate, and a
            //  ConcurrentDictionary removal is safe against both the lookups and the enumeration:
            //  a snapshot in flight may or may not include this vehicle, the same latitude
            //  GetLatestSnapshot already documents for a concurrent admission. Nothing tears --
            //  the ring is not mutated here, only unpublished, so a reader holding a reference to
            //  it keeps reading a consistent buffer.
            return _rings.TryRemove(id, out _);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TelemetryFrame> Subscribe(CancellationToken cancellationToken)
    {
        //  Refused up front, because accepting it is a leak with no symptom. The registration
        //  below releases a subscription the caller never enumerates, and Register on a
        //  non-cancellable token is a documented no-op -- it hooks nothing up. `Subscribe(default)`
        //  would therefore hand back a stream whose only release path is an enumeration that may
        //  never start, invisibly from both sides: the caller sees a working subscription, the
        //  store sees a channel it will TryWrite to 120 times a second, inside its own write gate,
        //  for the rest of the process.
        //
        //  Rejected rather than worked around. A store-owned CancellationTokenSource per
        //  subscription would make `default` safe, but only by moving the problem -- something
        //  still has to decide when to cancel it, and nothing here knows. Requiring a token tied
        //  to whatever owns the subscription beats inventing a lifetime the store cannot observe.
        //  It costs callers nothing: the SSE endpoint has ctx.RequestAborted, and anything else
        //  writes `using CancellationTokenSource cts = new();` instead of `default`.
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

        //  NOT an iterator, and the absence of `yield` here is load-bearing rather than
        //  incidental. An `async IAsyncEnumerable` body does not begin executing until the
        //  consumer's first MoveNextAsync, so a Subscribe written that way would create no channel
        //  and register no subscriber at the moment it returned: every frame written between the
        //  call and the start of enumeration would be lost. That is the same gap the seeding
        //  exists to close, reopened one layer down, and a test that enumerates immediately would
        //  never see it. Splitting eager registration (here) from lazy reading (Drain) is what
        //  keeps both halves honest.
        Channel<TelemetryFrame> channel = Channel.CreateBounded<TelemetryFrame>(
            new BoundedChannelOptions(ITelemetryStore.SubscriberBufferCapacity)
            {
                //  Drop-oldest is what makes the fan-out in Write non-blocking, and the direction
                //  is a hazard decision, not a performance one: this is a state stream, so a
                //  subscriber that resumes needs where the vehicle is, not a replay of where it
                //  was. Dropping the newest would show a smooth, complete, permanently stale
                //  picture -- HAZ-01 exactly.
                FullMode = BoundedChannelFullMode.DropOldest,

                //  One `await foreach` consumes this channel; any number of ingest threads may
                //  fan out to it. Both are assertions the channel implementation is allowed to
                //  optimise against, so they have to be true: SingleReader holds because the
                //  channel is created per subscription and only Drain ever reads it.
                SingleReader = true,
                SingleWriter = false,
            });

        //  Seed and register together, under the gate Write also holds for its whole body. That is
        //  what makes "no gap, and never an older frame after a newer one" true, and neither half
        //  achieves it alone: seed-then-register with a write slipping between loses that frame
        //  (the subscriber's newest state is older than the store's), and register-then-seed with a
        //  write slipping between delivers the stale seed after the live frame that superseded it.
        //  Holding the gate is what removes the "between", so the two orderings become equivalent
        //  and the choice below is free.
        lock (_subscriberGate)
        {
            //  Calling GetLatestSnapshot from in here is exact, even though the same call is
            //  approximate from outside. Only Write mutates `_rings`, and Write cannot be running:
            //  it needs this gate. So the enumeration it warns about cannot race an admission.
            //
            //  No lock-order inversion, either. This takes `_subscriberGate` and then each ring's
            //  `_gate` (inside Latest); Write takes them in that same order. The readers take only
            //  a ring's lock and never this one, so there is no cycle to close.
            foreach (TelemetryFrame frame in GetLatestSnapshot())
            {
                //  TryWrite on a drop-oldest channel cannot fail, and the seed cannot overflow it
                //  in any case: at most MaxVehicles (12) frames into a 256-slot buffer.
                channel.Writer.TryWrite(frame);
            }

            //  Seed first, then register, so the channel already holds the past before it can
            //  receive the future. Under this gate the reverse order is equally correct -- but if
            //  anyone ever narrows the lock, this order degrades into a duplicate frame while the
            //  other degrades into a lost one, and a duplicate is the far cheaper bug.
            _subscribers.Add(channel);
        }

        //  The subscription is live as of this line, before the caller has touched the enumerator.
        //  Drain only reads what the channel has already been accumulating.
        //
        //  Which is exactly why cancellation cannot be left to Drain's finally: that finally runs
        //  only if the caller enumerates, and the subscription exists whether it does or not. The
        //  shape is not hypothetical -- the SSE endpoint does
        //  `var stream = store.Subscribe(ctx.RequestAborted);` and can still fail before its first
        //  `await foreach`: a header flush to a client that has already gone, an early
        //  `Results.BadRequest`, a losing branch of a Task.WhenAny. On any of those the channel
        //  would stay in `_subscribers` for the life of the store, taking a TryWrite per frame at
        //  120 f/s inside the store-wide gate and pinning a buffer nobody will ever read. This
        //  registration is what makes "cancelling unregisters the subscriber" true on the
        //  un-enumerated path -- and only because the guard at the top of this method has already
        //  ruled out the token for which Register quietly does nothing. One mechanism, two halves.
        //
        //  Registered outside the gate deliberately. An already-cancelled token runs the callback
        //  synchronously on this thread, and the callback takes the gate itself -- correct, and it
        //  simply undoes the registration a few lines above, but only because nothing is held here.
        CancellationTokenRegistration registration =
            cancellationToken.Register(() => Unsubscribe(channel));

        //  Boxed because the flag has to be shared, and an `int` parameter would not be. Each call
        //  to GetAsyncEnumerator after the first produces a fresh copy of the iterator's state
        //  machine, fields and all, so a value captured by value would read zero every time; the
        //  box is one reference the copies have in common. See the guard at the top of Drain.
        StrongBox<int> enumerated = new(0);

        return Drain(channel, registration, enumerated, cancellationToken);
    }

    /// <summary>
    /// Releases one subscription: out of the fan-out list first, then the channel completed.
    /// </summary>
    /// <remarks>
    /// Both paths that can end a subscription call this -- the enumeration unwinding in
    /// <see cref="Drain"/>, and the cancellation callback registered in <see cref="Subscribe"/> --
    /// and for a cancelled enumeration both of them run, on different threads. That is safe because
    /// each step is idempotent: <see cref="List{T}.Remove"/> returns false the second time and
    /// <see cref="ChannelWriter{T}.TryComplete"/> does the same.
    /// <para>
    /// The order is the load-bearing part, and it is the same order the fan-out in
    /// <see cref="Write"/> depends on. Removing under the gate <i>before</i> completing the writer
    /// is what lets <c>Write</c> treat a false from <c>TryWrite</c> as impossible rather than as a
    /// case it has to handle: any fan-out in flight holds the gate this acquires, so it has
    /// finished by the time the writer is completed. Completing outside the gate keeps the
    /// store-wide critical section down to one list removal.
    /// </para>
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
    /// Drains one subscriber's channel, and unregisters it however the enumeration ends.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Subscribe"/> so that the registration above is eager and only the
    /// reading is lazy. Both tokens matter: the one passed to <see cref="Subscribe"/> and the one
    /// a consumer may supply via <c>WithCancellation</c>, which is what
    /// <see cref="EnumeratorCancellationAttribute"/> plumbs in.
    /// </remarks>
    private async IAsyncEnumerable<TelemetryFrame> Drain(
        Channel<TelemetryFrame> channel,
        CancellationTokenRegistration registration,
        StrongBox<int> enumerated,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        //  One enumeration per Subscribe call, and the second is refused rather than served. It
        //  cannot be served: eager registration means the channel is the subscription, so there is
        //  no second stream to hand out -- and by the time anyone asks, the first enumerator's
        //  finally has already unregistered the channel and completed the writer. Left unguarded
        //  the second `await foreach` reads a completed channel and ends immediately, so a caller
        //  whose reconnect loop looks like `while (true) { try { await foreach ... } catch { } }`
        //  spins on a dead stream, receiving nothing and being told nothing. A caller who wants a
        //  second reader wants a second Subscribe; this says so at the point of the mistake.
        //
        //  The check is here rather than in a wrapper's GetAsyncEnumerator, which would catch it
        //  one call earlier, because a wrapper would have to re-link Subscribe's token with the one
        //  from WithCancellation by hand -- the exact CreateLinkedTokenSource the comment below
        //  avoids, and the thing most likely to leak. Throwing from the first MoveNextAsync is the
        //  cheaper end of that trade.
        if (Interlocked.Exchange(ref enumerated.Value, 1) != 0)
        {
            //  Before the try, so the rejected enumeration's finally does not run: unregistering
            //  here would tear down the first enumerator's live subscription.
            throw new InvalidOperationException(
                "This telemetry subscription has already been enumerated. ITelemetryStore.Subscribe "
                + "returns a single-use stream; call Subscribe again for a second reader.");
        }

        //  The two tokens are already combined, and not by anything written here. Passing
        //  Subscribe's token as the argument to an [EnumeratorCancellation] parameter makes the
        //  compiler capture it in the state machine; if a consumer later calls
        //  `WithCancellation(other)`, the generated GetAsyncEnumerator links the two into a fresh
        //  source and disposes it when the enumerator is disposed. A hand-rolled
        //  CreateLinkedTokenSource here would build a second, redundant one -- and be the thing
        //  most likely to leak, since it would need disposing on every exit path below.
        try
        {
            //  ReadAllAsync completes when the writer completes and throws
            //  OperationCanceledException when the token fires, which is exactly the contract
            //  ITelemetryStore.Subscribe documents. It is deliberately not caught: swallowing it
            //  would turn a cancelled stream into one that looks like it ended normally, and the
            //  SSE endpoint cannot tell "client went away" from "fleet went quiet".
            await foreach (TelemetryFrame frame in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            //  Unregister, not Dispose. Dispose blocks until a callback already running on another
            //  thread returns, and that callback is trying to take `_subscriberGate` -- which is
            //  fine here, where nothing is held, but it is a deadlock waiting for someone to move
            //  this inside the lock below. Unregister never waits, and not waiting costs nothing:
            //  if the callback is mid-flight it is doing the same idempotent work Unsubscribe does
            //  next. Cancelling the enumeration therefore runs both, in either order, harmlessly.
            registration.Unregister();

            //  A finally, not a tail after the loop, because the loop's normal exit is the rare
            //  case. Cancellation throws, and abandoning an `await foreach` disposes the enumerator
            //  mid-iteration; neither reaches a statement placed after the loop. A subscriber left
            //  in the list on those paths is a leak whose only symptom is a slow memory climb after
            //  a few thousand SSE reconnects -- and Write would keep fanning frames into a channel
            //  nobody reads.
            Unsubscribe(channel);
        }
    }

    /// <summary>
    /// One vehicle's fixed-size circular buffer of frames, oldest evicted first.
    /// </summary>
    /// <remarks>
    /// The array is allocated once, when the vehicle is admitted, and never resized -- twelve of
    /// these is about 57 KB of references, bounded by design, and the write path allocates nothing.
    /// <para>
    /// <b>What the per-vehicle lock buys, precisely.</b> Not writer-versus-writer exclusion: those
    /// are already serialised on <see cref="_subscriberGate"/> one level up, so this lock is never
    /// contended by two appends. It buys reader-versus-writer exclusion, which is the case that
    /// remains genuinely concurrent -- <see cref="GetLatest"/>, <see cref="GetHistory"/> and
    /// <see cref="GetLatestSnapshot"/> take no store-wide gate at all, and this is the only thing
    /// standing between them and a buffer mid-append.
    /// </para>
    /// <para>
    /// <b>What is per vehicle is the eviction, not the contention.</b> Each ring owning its own
    /// buffer is what keeps a 10 Hz vehicle from pushing a quieter one's minute of history out; a
    /// single store-wide buffer would make every vehicle's retained history a function of how
    /// chatty its neighbours are.
    /// </para>
    /// </remarks>
    private sealed class VehicleRing
    {
        private readonly Lock _gate = new();

        private readonly TelemetryFrame[] _frames =
            new TelemetryFrame[ITelemetryStore.HistoryDepthPerVehicle];

        /// <summary>
        /// Where the next frame goes -- one past the newest, modulo the buffer length.
        /// </summary>
        /// <remarks>
        /// A next-write cursor rather than a newest-frame index, so that the empty ring needs no
        /// sentinel: 0 is a valid starting value with no frames present, whereas "index of newest"
        /// would have to start at -1 and be special-cased in three places.
        /// </remarks>
        private int _next;

        /// <summary>
        /// How many slots hold a real frame -- climbs to the buffer length and then stops.
        /// </summary>
        /// <remarks>
        /// Tracked rather than derived, because it cannot be derived: once the ring has wrapped,
        /// <see cref="_next"/> is 0 both for an empty ring and for a full one that has just come
        /// round, so the cursor alone cannot distinguish them. Saturating here also means
        /// <see cref="Snapshot"/> never has to ask whether the ring is full -- the count answers it.
        /// </remarks>
        private int _count;

        /// <summary>Records a frame, evicting the oldest if the buffer is full.</summary>
        public void Append(TelemetryFrame frame)
        {
            lock (_gate)
            {
                //  Eviction is a consequence of the assignment, not a separate step: at capacity
                //  `_next` points at the oldest frame, so overwriting it in place is the eviction.
                //  No shifting, no allocation, no work proportional to the buffer -- which is what
                //  makes the 10 Hz per-vehicle ceiling a non-event.
                _frames[_next] = frame;
                _next = (_next + 1) % _frames.Length;

                //  Saturate. Past capacity every append evicts one frame and adds one, so the live
                //  count stops climbing while `_next` keeps moving.
                if (_count < _frames.Length)
                {
                    _count++;
                }
            }
        }

        /// <summary>Gets the most recently appended frame.</summary>
        /// <remarks>
        /// Non-nullable, which is a claim about <see cref="Write"/> and not merely a
        /// preference: the admission path appends before publishing the ring into
        /// <see cref="_rings"/>, so a ring any other thread can reach already holds a frame. Were
        /// the ring published first and appended second, a concurrent
        /// <see cref="GetLatestSnapshot"/> could observe it empty, and this would have to be
        /// nullable -- at which point <see cref="GetLatest"/>'s null would mean two different
        /// things (unknown vehicle, or known vehicle with nothing to show) and its callers could no
        /// longer tell them apart. The type is where that gets settled once.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The ring holds no frames, which means the invariant above has been broken by a change to
        /// the admission path.
        /// </exception>
        public TelemetryFrame Latest
        {
            get
            {
                lock (_gate)
                {
                    //  Guarding an unreachable case on purpose. Without it the empty ring returns
                    //  `_frames[length - 1]`, a default null that the nullable annotations promise
                    //  is not there, and the failure surfaces later as a NullReferenceException in
                    //  whichever consumer happened to dereference it -- a stack trace pointing at
                    //  the API rather than at the store that broke its own rule. One comparison
                    //  under a lock already held is not a cost worth weighing against that.
                    if (_count == 0)
                    {
                        throw new InvalidOperationException(
                            "A published VehicleRing must never be empty: admission appends the "
                            + "first frame before inserting the ring into `_rings`. Reaching this "
                            + "means that order was changed.");
                    }

                    //  `_next` is one past the newest, so step back one -- and add the length
                    //  before taking the remainder, because C# `%` keeps the sign of the left
                    //  operand and a bare -1 % 600 is -1, not 599.
                    return _frames[(_next - 1 + _frames.Length) % _frames.Length];
                }
            }
        }

        /// <summary>Copies the retained frames out, oldest first.</summary>
        /// <remarks>
        /// Copies under <see cref="_gate"/>. Returning anything that aliases <c>_frames</c> would
        /// let a caller read a slot that a concurrent append had already overwritten -- a frame
        /// from a minute ago appearing in the middle of a fresh history.
        /// </remarks>
        public IReadOnlyList<TelemetryFrame> Snapshot()
        {
            lock (_gate)
            {
                //  Exactly `_count` long, so a young vehicle's history is not padded with nulls
                //  that the return type says cannot be there.
                TelemetryFrame[] copy = new TelemetryFrame[_count];

                //  The oldest live frame is `_count` steps behind the cursor. That one expression
                //  covers both cases the buffer can be in, which is why there is no `if` here:
                //  before the ring wraps, `_next == _count`, so this is 0 and the run is the plain
                //  prefix `_frames[0.._count]`; after it wraps, `_count` is the full length, so
                //  this is `_next` -- the slot the next append will evict, which is by definition
                //  the oldest thing still present.
                int oldest = (_next - _count + _frames.Length) % _frames.Length;

                //  Then it is two straight copies, in order, oldest first: the run from `oldest` to
                //  wherever it stops, and the part that wrapped round to the front. Min is what
                //  stops the first copy -- at the end of the buffer if the run wrapped, at the end
                //  of the live data if it did not, in which case the second copy has length zero
                //  and does nothing. Array.Copy rather than a loop because it is one bulk reference
                //  move under a lock the writers also want.
                int firstRun = Math.Min(_count, _frames.Length - oldest);
                Array.Copy(_frames, oldest, copy, 0, firstRun);
                Array.Copy(_frames, 0, copy, firstRun, _count - firstRun);

                //  A fresh array escapes, never `_frames` itself and never a view over it. Handing
                //  back an alias would let a caller enumerating a track read a slot that a
                //  concurrent append had already overwritten -- a minute-old position appearing in
                //  the middle of a fresh history, with nothing in the data to mark it as wrong.
                return copy;
            }
        }
    }
}
