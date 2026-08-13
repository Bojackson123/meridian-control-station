using Mcs.Core;

namespace Mcs.Adapters.Tests;

/// <summary>
/// An <see cref="ITelemetryStore"/> that records what it was handed and lets a test wait for it.
/// </summary>
/// <remarks>
/// The adapter writes from a socket read loop on the thread pool, so <see cref="Writes"/> is taken
/// under a lock and handed back as a copy: the test thread reads it while that loop may still be
/// running. Waiting for the write to have happened at all is
/// <see cref="MavlinkAdapterHarness.WaitUntilAsync"/>'s job, and the remarks there say why this
/// class deliberately does not signal it.
/// <para>
/// <b>It records the clock as well as the frame.</b> MCS-005 is a claim about <i>which</i> instant a
/// frame carries, and the only way to see that the receipt was stamped before the decode rather
/// than after it is to compare the stamp against what the clock said when the write happened -- and
/// the store is the only thing present at that moment. Reading
/// <see cref="TimeProvider.GetUtcNow"/> does not advance <see cref="SteppingClock"/>, so observing
/// costs nothing that the assertion then has to account for.
/// </para>
/// <para>
/// Only <see cref="Write"/> is implemented. The others throw rather than returning empty: an adapter
/// reaching for one of them would be doing something this fake cannot represent, and a quiet empty
/// answer would let that go unnoticed.
/// </para>
/// </remarks>
/// <param name="timeProvider">The clock whose opinion is recorded beside each frame.</param>
internal sealed class RecordingTelemetryStore(TimeProvider timeProvider) : ITelemetryStore
{
    private readonly Lock _gate = new();
    private readonly List<RecordedWrite> _writes = [];

    /// <summary>
    /// Gets or sets whether every write is refused as if the fleet were full. The frame is still
    /// recorded first, so a refused attempt is visible to a test rather than leaving no trace.
    /// </summary>
    internal bool RejectEveryVehicle { get; set; }

    /// <summary>Gets what the store was handed, in order.</summary>
    internal IReadOnlyList<RecordedWrite> Writes
    {
        get
        {
            lock (_gate)
            {
                return [.. _writes];
            }
        }
    }

    /// <inheritdoc />
    public void Write(TelemetryFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            _writes.Add(new RecordedWrite(frame, timeProvider.GetUtcNow()));
        }

        //  Recorded above and refused here, in that order: the adapter's behaviour on a refusal is
        //  what one of these tests is about, and a store that threw before recording would leave it
        //  with nothing to look at.
        if (RejectEveryVehicle)
        {
            throw new TelemetryStoreCapacityExceededException(frame.Telemetry.Id);
        }
    }

    /// <inheritdoc />
    public TelemetryFrame? GetLatest(VehicleId id) => throw new NotSupportedException();

    /// <inheritdoc />
    public IReadOnlyList<TelemetryFrame> GetLatestSnapshot() => throw new NotSupportedException();

    /// <inheritdoc />
    public IReadOnlyList<TelemetryFrame> GetHistory(VehicleId id) => throw new NotSupportedException();

    /// <inheritdoc />
    public bool Forget(VehicleId id) => throw new NotSupportedException();

    /// <inheritdoc />
    public IAsyncEnumerable<TelemetryFrame> Subscribe(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>A frame the store was handed, and what the clock read at that moment.</summary>
internal sealed record RecordedWrite(TelemetryFrame Frame, DateTimeOffset ObservedUtcNow);
