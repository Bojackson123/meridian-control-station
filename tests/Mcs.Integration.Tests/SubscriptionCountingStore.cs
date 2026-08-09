using Mcs.Core;

namespace Mcs.Integration.Tests;

/// <summary>
/// Wraps the real store and counts subscriptions that have been opened but not yet released.
/// </summary>
/// <remarks>
/// A leaked subscription is invisible from outside: the store holds a bounded channel nobody reads,
/// and the only symptom is memory climbing across a demo's worth of page reloads. Rather than give
/// <c>InMemoryTelemetryStore</c> a counter that exists only for a test -- <c>Mcs.Core</c> has no
/// <c>InternalsVisibleTo</c> and its project file is empty on purpose -- this decorates it. Every
/// other member forwards, so the request is still served by the real implementation.
/// </remarks>
internal sealed class SubscriptionCountingStore : ITelemetryStore
{
    private readonly ITelemetryStore _inner;

    private int _openSubscriptions;

    public SubscriptionCountingStore(ITelemetryStore inner) => _inner = inner;

    /// <summary>Gets how many subscriptions are currently open.</summary>
    public int OpenSubscriptions => Volatile.Read(ref _openSubscriptions);

    /// <inheritdoc />
    public IAsyncEnumerable<TelemetryFrame> Subscribe(CancellationToken cancellationToken)
    {
        //  Counted here rather than at enumeration, because the store registers eagerly: the
        //  subscription is live the moment this returns, whether or not anyone reads from it.
        Interlocked.Increment(ref _openSubscriptions);

        return new CountedSubscription(_inner.Subscribe(cancellationToken), this);
    }

    /// <inheritdoc />
    public void Write(TelemetryFrame frame) => _inner.Write(frame);

    /// <inheritdoc />
    public TelemetryFrame? GetLatest(VehicleId id) => _inner.GetLatest(id);

    /// <inheritdoc />
    public IReadOnlyList<TelemetryFrame> GetLatestSnapshot() => _inner.GetLatestSnapshot();

    /// <inheritdoc />
    public IReadOnlyList<TelemetryFrame> GetHistory(VehicleId id) => _inner.GetHistory(id);

    /// <inheritdoc />
    public bool Forget(VehicleId id) => _inner.Forget(id);

    private void Release() => Interlocked.Decrement(ref _openSubscriptions);

    private sealed class CountedSubscription : IAsyncEnumerable<TelemetryFrame>
    {
        private readonly IAsyncEnumerable<TelemetryFrame> _inner;
        private readonly SubscriptionCountingStore _owner;

        public CountedSubscription(
            IAsyncEnumerable<TelemetryFrame> inner, SubscriptionCountingStore owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public IAsyncEnumerator<TelemetryFrame> GetAsyncEnumerator(
            CancellationToken cancellationToken) =>
            new CountedEnumerator(_inner.GetAsyncEnumerator(cancellationToken), _owner);
    }

    private sealed class CountedEnumerator : IAsyncEnumerator<TelemetryFrame>
    {
        private readonly IAsyncEnumerator<TelemetryFrame> _inner;
        private readonly SubscriptionCountingStore _owner;

        public CountedEnumerator(
            IAsyncEnumerator<TelemetryFrame> inner, SubscriptionCountingStore owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public TelemetryFrame Current => _inner.Current;

        public ValueTask<bool> MoveNextAsync() => _inner.MoveNextAsync();

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();

            //  After the inner disposal, so the count only drops once the store has actually let
            //  go -- otherwise a test could see zero while the subscriber is still registered.
            _owner.Release();
        }
    }
}
