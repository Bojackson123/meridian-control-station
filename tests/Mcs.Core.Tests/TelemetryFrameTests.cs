using System.Reflection;

namespace Mcs.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="TelemetryFrame"/>.
/// </summary>
/// <remarks>
/// A frame is a vehicle's claim paired with the station's own observation of when it arrived, and
/// the split is by provenance: <see cref="TelemetryFrame.Telemetry"/> is untrusted,
/// <see cref="TelemetryFrame.ReceivedAtUtc"/> is the single trusted time base for MCS-002's
/// staleness and, later, for the deconfliction windows.
/// <para>
/// Most of what this type promises is enforced by what it does <i>not</i> expose, so a good half
/// of the cases below are reflection over the public surface -- the construction routes that must
/// not exist, the setters that would let a frame restamp itself, and the staleness members that
/// deliberately live in the console layer instead. Those are the assertions that fail if someone
/// later adds a convenience constructor and reopens MCS-005.
/// </para>
/// </remarks>
public class TelemetryFrameTests
{
    /// <summary>The one supported way to obtain a frame, used by every case here.</summary>
    private static TelemetryFrame Frame(
        VehicleTelemetry? telemetry = null, DateTimeOffset? arrival = null) =>
        new TelemetryIngest(new FakeClock(arrival ?? FakeClock.Arrival))
            .BeginReceive()
            .Complete(telemetry ?? TelemetrySamples.Telemetry());

    // --- What a frame carries -------------------------------------------------------------------

    [Fact]
    public void Frame_PairsTheReportWithTheInstantItArrived()
    {
        VehicleTelemetry telemetry = TelemetrySamples.Telemetry();

        TelemetryFrame frame = Frame(telemetry);

        Assert.Same(telemetry, frame.Telemetry);
        Assert.Equal(FakeClock.Arrival, frame.ReceivedAtUtc);
    }

    [Fact]
    public void Frame_ReachesTheVehiclesClaimsThroughTelemetry()
    {
        // The shape the API's flattening DTO and the console both read: the station's time at the
        // top level, everything the vehicle said one level down. Which of the two you are holding
        // is what answers "can I trust this value?".
        TelemetryFrame frame = Frame();

        Assert.Equal(VehicleId.From(TelemetrySamples.Id), frame.Telemetry.Id);
        Assert.Equal(51.5074, frame.Telemetry.LatitudeDegrees);
        Assert.Equal(LinkStatus.Healthy, frame.Telemetry.LinkStatus);
    }

    // --- MCS-005: there is no other way to make one ---------------------------------------------

    [Fact]
    public void Type_ExposesNoPublicConstructor()
    {
        // Nothing an autocomplete list will offer and a caller will use in good faith. The
        // private constructor and the internal Create between them mean a frame outside
        // Mcs.Core can only have come from a receipt -- which can only have come from an arrival.
        Assert.Empty(
            typeof(TelemetryFrame).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void Create_IsInternalAndStatic()
    {
        // This is the method that takes a timestamp as a parameter, which is exactly what the
        // public surface must not do -- so visibility is what makes MCS-005 enforceable here.
        MethodInfo? create = typeof(TelemetryFrame)
            .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(create);
        Assert.True(create.IsAssembly, "TelemetryFrame.Create must stay internal.");
    }

    [Fact]
    public void Assembly_ExposesExactlyOnePublicMemberThatYieldsAFrame()
    {
        // The claim the remarks make in prose, checked across the whole public surface rather
        // than on this type alone: outside Mcs.Core there is no expression that produces a frame
        // without first having recorded an arrival. A convenience factory added anywhere in the
        // assembly fails here.
        MethodInfo[] producers = [.. typeof(TelemetryFrame).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(TelemetryFrame))
            // The record's synthesized clone method. Unspeakable in C# source, and reachable only
            // through a `with` expression -- which cannot change anything on a get-only record.
            .Where(m => !m.Name.StartsWith('<'))];

        MethodInfo only = Assert.Single(producers);
        Assert.Equal(nameof(TelemetryReceipt.Complete), only.Name);
        Assert.Equal(typeof(TelemetryReceipt), only.DeclaringType);
    }

    [Fact]
    public void Type_ExposesNoPublicSetters()
    {
        // Get-only, so `frame with { ReceivedAtUtc = clock.GetUtcNow() }` does not compile. That
        // is precisely the hole a constructor default would leave open: an object held from an
        // earlier second quietly claiming to be new.
        PropertyInfo[] properties =
            typeof(TelemetryFrame).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.NotEmpty(properties);
        Assert.All(properties, p => Assert.Null(p.SetMethod));
    }

    [Fact]
    public void WithExpression_CopiesTheArrivalTimeRatherThanRefreshingIt()
    {
        // The only `with` the type permits is an empty one, and it produces an equal frame. A
        // frame cannot restamp itself -- the copy is as old as the original.
        TelemetryFrame frame = Frame();

        TelemetryFrame copy = frame with { };

        Assert.Equal(frame.ReceivedAtUtc, copy.ReceivedAtUtc);
        Assert.Equal(frame, copy);
    }

    [Fact]
    public void Type_HasNoStalenessMembers()
    {
        // A deliberate absence, asserted so it stays deliberate. IsStale and Age both need a
        // "now", and a value that reads a clock gives a different answer each time it is asked --
        // untestable and unloggable. Staleness evaluation belongs to the console layer, which
        // holds the TimeProvider; this type's job is to record the one fact it needs.
        Assert.DoesNotContain(
            typeof(TelemetryFrame).GetMembers(BindingFlags.Instance | BindingFlags.Public),
            m => m.Name.Contains("Stale", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Age", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Elapsed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Type_IsSealed() => Assert.True(typeof(TelemetryFrame).IsSealed);

    [Fact]
    public void Type_AllInstanceFields_AreReadOnly()
    {
        // Backs the immutability the rest of the station assumes: a frame crosses threads
        // between the feed and the SSE readers with no lock anywhere.
        FieldInfo[] fields = typeof(TelemetryFrame)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.True(f.IsInitOnly, $"{f.Name} is not readonly."));
    }

    // --- ToString --------------------------------------------------------------------------------

    [Fact]
    public void ToString_WritesTheTimestampInRoundTripFormat()
    {
        // "O" is unambiguous, sortable as text, and what the station's JSON logs and any
        // downstream parser expect. A frame stamped at midday UTC must say so, offset included.
        Assert.Contains(
            "ReceivedAtUtc = 2026-08-08T12:00:00.0000000+00:00",
            Frame().ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_NestsTheReportInsideTheFrame() =>
        Assert.Equal(
            $"TelemetryFrame {{ Telemetry = {TelemetrySamples.TelemetryText}, "
            + "ReceivedAtUtc = 2026-08-08T12:00:00.0000000+00:00 }",
            Frame().ToString());

    [Fact]
    public void ToString_SubSecondArrival_KeepsSevenFractionalDigits()
    {
        // Frames arrive several times a second, so a format that rounded to whole seconds would
        // make two consecutive arrivals indistinguishable in a log -- exactly where ordering
        // questions get asked.
        DateTimeOffset arrival = FakeClock.Arrival.AddTicks(1234567);

        Assert.Contains(
            "ReceivedAtUtc = 2026-08-08T12:00:00.1234567+00:00",
            Frame(arrival: arrival).ToString(),
            StringComparison.Ordinal);
    }

    // --- Equality ---------------------------------------------------------------------------------

    [Fact]
    public void Equality_SameReportAndSameArrival_AreEqualWithSameHashCode()
    {
        // Two receipts, one instant, equal payloads: value equality, so nothing downstream has to
        // care which receipt produced which frame.
        TelemetryFrame a = Frame();
        TelemetryFrame b = Frame();

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_SameReportDifferentArrival_AreNotEqual()
    {
        // The case that matters for a ring buffer: the same vehicle re-sending an identical
        // report a second later is a new frame, not a duplicate to be collapsed. Staleness is
        // derived from that second.
        Assert.NotEqual(
            Frame(),
            Frame(arrival: FakeClock.Arrival.AddSeconds(1)));
    }

    [Fact]
    public void Equality_DifferentReportSameArrival_AreNotEqual() =>
        Assert.NotEqual(
            Frame(),
            Frame(TelemetrySamples.Telemetry(id: VehicleId.From("UAV-02"))));

    [Fact]
    public void Equality_ArrivalsOneTickApart_AreNotEqual()
    {
        // Full DateTimeOffset precision participates in equality; nothing is truncated to
        // milliseconds on the way in.
        Assert.NotEqual(
            Frame(),
            Frame(arrival: FakeClock.Arrival.AddTicks(1)));
    }
}

/// <summary>
/// <see cref="TelemetryFrame"/> tests that mutate
/// <see cref="System.Globalization.CultureInfo.CurrentCulture"/>.
/// </summary>
[Collection(CultureCollection.Name)]
public class TelemetryFrameCultureTests
{
    [Fact]
    public void ToString_IsInvariant_RegardlessOfAmbientCulture()
    {
        // Why PrintMembers is overridden here as well as on VehicleTelemetry: the synthesized
        // version formats with the current culture, which would make a frame's logged timestamp
        // depend on the container's locale.
        using CultureScope _ = new("de-DE");

        TelemetryFrame frame = new TelemetryIngest(new FakeClock())
            .BeginReceive()
            .Complete(TelemetrySamples.Telemetry());

        Assert.Equal(
            $"TelemetryFrame {{ Telemetry = {TelemetrySamples.TelemetryText}, "
            + "ReceivedAtUtc = 2026-08-08T12:00:00.0000000+00:00 }",
            frame.ToString());
    }

    [Fact]
    public void ToString_UnderANonGregorianCalendar_StillWritesAnIsoTimestamp()
    {
        // The trap the "O" format exists to avoid: a culture whose default calendar is not
        // Gregorian would otherwise write a year no ISO-8601 parser accepts, and the JSON logs
        // are parsed by something downstream.
        using CultureScope _ = new("ar-SA");

        TelemetryFrame frame = new TelemetryIngest(new FakeClock())
            .BeginReceive()
            .Complete(TelemetrySamples.Telemetry());

        Assert.Contains(
            "ReceivedAtUtc = 2026-08-08T12:00:00.0000000+00:00",
            frame.ToString(),
            StringComparison.Ordinal);
    }
}
