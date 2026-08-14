using System.Globalization;
using System.Reflection;

namespace Mcs.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="Altitude"/> and <see cref="AltitudeReference"/>.
/// </summary>
/// <remarks>
/// These types exist to satisfy <b>MCS-004</b> -- <i>"the adapter interface shall reject any
/// position report that does not declare an altitude reference (MSL, AGL, or HAE)"</i> -- by
/// making the value and its datum inseparable, so the requirement is met at every call site at
/// once rather than by a runtime check somebody can forget.
/// <para>
/// The reference-rejection cases below are the direct verification of that requirement: an
/// altitude reference cannot be omitted, and these are the cases that close every route to
/// omitting one. The remaining cases cover unit conversion, finiteness, the <c>default</c>
/// sentinel, and formatting.
/// </para>
/// </remarks>
public class AltitudeTests
{
    // --- MCS-004: the reference cannot be omitted --------------------------------------------

    [Theory]
    [InlineData(0)]              // the uninitialised-struct sentinel
    [InlineData(99)]             // out-of-band cast
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [Verifies("MCS-004")]
    public void FromMeters_UndeclaredReference_ThrowsArgumentOutOfRange(int rawReference)
    {
        AltitudeReference reference = (AltitudeReference)rawReference;

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Altitude.FromMeters(100, reference));

        Assert.Equal("reference", ex.ParamName);
        Assert.Equal(reference, Assert.IsType<AltitudeReference>(ex.ActualValue));

        // The requirement id travels with the rejection, so a log line naming this exception is
        // traceable back to what it enforces without anyone consulting the source.
        Assert.Contains("MCS-004", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-1)]
    [Verifies("MCS-004")]
    public void FromFeet_UndeclaredReference_ThrowsArgumentOutOfRange(int rawReference)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Altitude.FromFeet(100, (AltitudeReference)rawReference));

        Assert.Equal("reference", ex.ParamName);
        Assert.Contains("MCS-004", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DeclaredReferences))]
    public void FromMeters_EveryDeclaredReference_IsAcceptedAndRoundTrips(
        AltitudeReference reference)
    {
        // Data-driven over the enum rather than hard-coded, so a member added later is covered
        // by this test automatically instead of silently escaping it.
        Altitude altitude = Altitude.FromMeters(1500.5, reference);

        Assert.Equal(reference, altitude.Reference);
        Assert.Equal(1500.5, altitude.Meters);
    }

    public static TheoryData<AltitudeReference> DeclaredReferences()
    {
        TheoryData<AltitudeReference> data = [];
        foreach (AltitudeReference reference in Enum.GetValues<AltitudeReference>())
        {
            data.Add(reference);
        }

        return data;
    }

    // --- FromMeters --------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1500.5)]
    [InlineData(-430.5)]        // Dead Sea shore: negative altitudes are legal
    [InlineData(-11034)]        // Challenger Deep
    [InlineData(8848.86)]       // Everest
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void FromMeters_FiniteValue_RoundTripsExactly(double meters)
    {
        // The type bounds finiteness, not the domain: there is no min/max altitude check, because
        // a plausible-range judgement belongs to the adapter that knows the vehicle.
        Assert.Equal(meters, Altitude.FromMeters(meters, AltitudeReference.Msl).Meters);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromMeters_NonFiniteValue_ThrowsArgumentOutOfRangeNamingMeters(double meters)
    {
        // Rejected because they are unrepresentable downstream, not as a domain judgement:
        // System.Text.Json throws on NaN and Infinity, so an unvalidated one would surface as a
        // failed telemetry response far from its cause.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Altitude.FromMeters(meters, AltitudeReference.Msl));

        Assert.Equal("meters", ex.ParamName);
    }

    // --- FromFeet ----------------------------------------------------------------------------

    [Fact]
    public void FromFeet_OneFoot_IsExactlyThePointThreeZeroFourEightFactor()
    {
        // 0.3048 is exact by the 1959 international definition of the foot, so this conversion
        // is not an approximation and does not need a tolerance.
        Assert.Equal(0.3048, Altitude.FromFeet(1, AltitudeReference.Msl).Meters);
    }

    [Fact]
    public void FromFeet_Zero_IsZeroMetres() =>
        Assert.Equal(0, Altitude.FromFeet(0, AltitudeReference.Agl).Meters);

    [Fact]
    public void FromFeet_KnownConversion_MatchesTheDocumentedExample()
    {
        // The XML doc's own example claims 4922.9 ft is about 1500.5 m. Asserted twice: exactly
        // against the same arithmetic the implementation performs, and loosely against the
        // physical figure -- a hand-typed decimal literal would only be a guess.
        Altitude altitude = Altitude.FromFeet(4922.9, AltitudeReference.Msl);

        Assert.Equal(4922.9 * 0.3048, altitude.Meters);
        Assert.Equal(1500.5, altitude.Meters, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(10000)]
    [InlineData(60000)]         // above any airspace this station will plan in
    [InlineData(-1000)]
    public void FromFeet_RoundTripsBackToFeetWithinTolerance(double feet)
    {
        double metres = Altitude.FromFeet(feet, AltitudeReference.Hae).Meters;

        Assert.Equal(feet, metres / 0.3048, 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromFeet_NonFiniteValue_ThrowsArgumentOutOfRangeNamingFeet(double feet)
    {
        // Names "feet", not "meters". This is the entire reason FromFeet re-checks finiteness
        // rather than delegating to the constructor -- an exception naming a metres-valued
        // parameter sends the reader looking for a call site that does not exist.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Altitude.FromFeet(feet, AltitudeReference.Msl));

        Assert.Equal("feet", ex.ParamName);
    }

    [Fact]
    public void FromFeet_MaxValue_DoesNotOverflow()
    {
        // Scaling by a factor below 1 cannot overflow, so a finite input always yields a finite
        // result. Pins the comment that justifies not re-checking after the multiplication.
        Altitude altitude = Altitude.FromFeet(double.MaxValue, AltitudeReference.Msl);

        Assert.True(double.IsFinite(altitude.Meters));
    }

    [Fact]
    public void FromFeet_AndFromMeters_AgreeOnTheSameAltitude() =>
        Assert.Equal(
            Altitude.FromMeters(0.3048, AltitudeReference.Msl),
            Altitude.FromFeet(1, AltitudeReference.Msl));

    // --- The default sentinel ----------------------------------------------------------------

    [Fact]
    public void Default_CanBeConstructedWithoutThrowing()
    {
        // The language will not let a struct refuse `default`. Construction stays quiet; reading
        // is what fails.
        Altitude uninitialised = default;

        Assert.Equal("Altitude(uninitialised)", uninitialised.ToString());
    }

    [Fact]
    public void Meters_OnDefault_ThrowsInvalidOperationException()
    {
        // A default instance would otherwise read back as 0 m, which is a plausible-looking
        // altitude -- and that plausibility is the hazard, because it is the one value a caller
        // would not question.
        Altitude uninitialised = default;

        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => { _ = uninitialised.Meters; });

        Assert.Contains("Altitude", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reference_OnDefault_ThrowsInvalidOperationException()
    {
        Altitude uninitialised = default;

        Assert.Throws<InvalidOperationException>(() => { _ = uninitialised.Reference; });
    }

    [Fact]
    public void ToString_OnDefault_ReturnsSentinelAndDoesNotThrow()
    {
        // Describable rather than throwing, for the same reason as VehicleId.ToString: an
        // exception raised from a diagnostic is worse than the bad value it was reporting.
        Altitude uninitialised = default;

        Assert.Equal("Altitude(uninitialised)", uninitialised.ToString());
    }

    [Fact]
    public void ToStringWithFormat_OnDefault_ReturnsSentinelIgnoringBothArguments()
    {
        Altitude uninitialised = default;

        Assert.Equal(
            "Altitude(uninitialised)",
            uninitialised.ToString("F3", CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Fact]
    public void Interpolation_OnDefault_ReturnsSentinelAndDoesNotThrow()
    {
        Altitude uninitialised = default;

        Assert.Equal("Altitude(uninitialised)", $"{uninitialised}");
    }

    // --- ToString(): invariant by default ----------------------------------------------------

    [Theory]
    [InlineData(1500.5, AltitudeReference.Msl, "1500.5 m Msl")]
    [InlineData(100, AltitudeReference.Agl, "100 m Agl")]
    [InlineData(1500.25, AltitudeReference.Hae, "1500.25 m Hae")]
    [InlineData(0, AltitudeReference.Msl, "0 m Msl")]
    [InlineData(-430.5, AltitudeReference.Msl, "-430.5 m Msl")]
    [InlineData(1500.256, AltitudeReference.Msl, "1500.26 m Msl")]
    public void ToString_FormatsValueUnitAndReference(
        double meters, AltitudeReference reference, string expected) =>
        Assert.Equal(expected, Altitude.FromMeters(meters, reference).ToString());

    [Theory]
    [MemberData(nameof(DeclaredReferences))]
    public void ToString_NamesTheReference(AltitudeReference reference) =>
        Assert.Contains(
            reference.ToString(),
            Altitude.FromMeters(100, reference).ToString(),
            StringComparison.Ordinal);

    // --- ToString(string?, IFormatProvider?): the IFormattable overload -----------------------

    [Fact]
    public void ToStringWithFormat_StandardFormat_AppliesToTheNumberOnly() =>
        Assert.Equal(
            "1500.500 m Msl",
            Altitude.FromMeters(1500.5, AltitudeReference.Msl)
                .ToString("F3", CultureInfo.InvariantCulture));

    [Fact]
    public void ToStringWithFormat_NullFormat_FallsBackToTwoDecimals() =>
        Assert.Equal(
            "1500.5 m Msl",
            Altitude.FromMeters(1500.5, AltitudeReference.Msl)
                .ToString(null, CultureInfo.InvariantCulture));

    [Fact]
    public void ToStringWithFormat_FrenchCulture_ChangesTheNumberButNotTheUnitOrReference()
    {
        // Only the number ever sees the provider: the unit is a literal and the reference is an
        // enum name, so both are culture-independent by construction.
        string formatted = Altitude.FromMeters(1500.5, AltitudeReference.Msl)
            .ToString(null, CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal("1500,5 m Msl", formatted);
    }

    // --- Equality and hashing ----------------------------------------------------------------

    [Fact]
    public void Equality_SameValueAndReference_AreEqualWithSameHashCode()
    {
        Altitude a = Altitude.FromMeters(1500.5, AltitudeReference.Msl);
        Altitude b = Altitude.FromMeters(1500.5, AltitudeReference.Msl);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_SameValueDifferentReference_AreNotEqual()
    {
        // MCS-004 expressed as equality: 100 m AGL is not 100 m MSL, and nothing short of terrain
        // data makes them interchangeable.
        Assert.NotEqual(
            Altitude.FromMeters(100, AltitudeReference.Msl),
            Altitude.FromMeters(100, AltitudeReference.Agl));
    }

    [Fact]
    public void Equality_DifferentValueSameReference_AreNotEqual() =>
        Assert.NotEqual(
            Altitude.FromMeters(100, AltitudeReference.Msl),
            Altitude.FromMeters(101, AltitudeReference.Msl));

    [Fact]
    public void Equality_SignedZero_IsEqualWithMatchingHashCode()
    {
        // Characterisation, not a designed behaviour: record-struct equality delegates to
        // double.Equals, where -0.0 equals 0.0, and .NET normalises negative zero when hashing.
        // Recorded so a future change to either is visible rather than surprising.
        Altitude positive = Altitude.FromMeters(0.0, AltitudeReference.Msl);
        Altitude negative = Altitude.FromMeters(-0.0, AltitudeReference.Msl);

        Assert.Equal(positive, negative);
        Assert.Equal(positive.GetHashCode(), negative.GetHashCode());
    }

    [Fact]
    public void Equality_DefaultInstances_AreEqualWithoutThrowing()
    {
        Altitude a = default;
        Altitude b = default;

        // Equality reads the backing fields, not the guarded properties, so comparing two
        // uninitialised altitudes does not throw. Characterisation: worth knowing before the
        // store starts comparing frames.
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DefaultAndValid_AreNotEqual()
    {
        Altitude uninitialised = default;

        Assert.NotEqual(Altitude.FromMeters(0, AltitudeReference.Msl), uninitialised);
    }

    // --- Structural invariants ---------------------------------------------------------------

    [Fact]
    public void AltitudeReference_HasNoZeroMember()
    {
        // The whole MCS-004 mechanism. A struct field that was never assigned reads back as 0,
        // so leaving 0 undefined is what stops an uninitialised altitude from silently claiming
        // MSL. Adding `Unknown = 0` later would disarm it -- and this fails immediately if
        // anyone does, rather than much later, when conversion arrives.
        Assert.DoesNotContain(Enum.GetValues<AltitudeReference>(), r => (int)r == 0);
    }

    [Fact]
    public void AltitudeReference_DeclaresTheThreeExpectedDatums()
    {
        AltitudeReference[] declared = Enum.GetValues<AltitudeReference>();

        Assert.Equal(3, declared.Length);
        Assert.Contains(AltitudeReference.Msl, declared);
        Assert.Contains(AltitudeReference.Agl, declared);
        Assert.Contains(AltitudeReference.Hae, declared);
    }

    [Fact]
    public void AltitudeReference_NumericValues_AreStable()
    {
        // Pinned because renumbering would silently change the wire contract for anything that
        // ever serialises the underlying number. The API is meant to emit the name instead --
        // this is the belt to that braces.
        Assert.Equal(1, (int)AltitudeReference.Msl);
        Assert.Equal(2, (int)AltitudeReference.Agl);
        Assert.Equal(3, (int)AltitudeReference.Hae);
    }

    [Fact]
    public void Type_ImplementsIFormattable() =>
        Assert.True(typeof(Altitude).IsAssignableTo(typeof(IFormattable)));

    [Fact]
    public void Type_DoesNotImplementIComparable()
    {
        // A deliberate absence, asserted so it stays deliberate. Ordering is undefined across
        // references -- 100 m AGL may be above or below 100 m MSL depending on terrain nobody
        // has loaded yet -- so a CompareTo would either lie for mixed pairs or throw from inside
        // Sort, where the caller cannot see it coming. Comparison belongs on whatever service
        // ends up holding the terrain.
        Assert.False(typeof(Altitude).IsAssignableTo(typeof(IComparable)));
        Assert.False(typeof(Altitude).IsAssignableTo(typeof(IComparable<Altitude>)));
    }

    [Fact]
    public void Type_IsValueTypeImplementingIEquatable()
    {
        Assert.True(typeof(Altitude).IsValueType);
        Assert.True(typeof(Altitude).IsAssignableTo(typeof(IEquatable<Altitude>)));
    }

    [Fact]
    public void Type_AllInstanceFields_AreReadOnly()
    {
        // Backs the immutability the rest of the station assumes: a value crosses threads
        // between the feed and the SSE readers with no lock anywhere.
        FieldInfo[] fields = typeof(Altitude)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.True(f.IsInitOnly, $"{f.Name} is not readonly."));
    }

    [Fact]
    public void Type_ExposesNoPublicConstructor()
    {
        // FromMeters and FromFeet are the complete list of ways a valid instance comes into
        // being, and each names its unit at the call site. A public constructor here would put
        // `new Altitude(120, Agl)` back -- which is exactly how a feet-valued sensor reading
        // gets stored as metres.
        Assert.Empty(typeof(Altitude).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void Type_ExposesNoFeetAccessor()
    {
        // The stored value is metres. A type that could hand back either unit would put the
        // ambiguity the factories exist to remove straight back.
        Assert.DoesNotContain(
            typeof(Altitude).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            p => p.Name.Contains("Feet", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// <see cref="Altitude"/> tests that mutate <see cref="CultureInfo.CurrentCulture"/>.
/// </summary>
[Collection(CultureCollection.Name)]
public class AltitudeCultureTests
{
    [Fact]
    public void ToString_IsInvariant_RegardlessOfAmbientCulture()
    {
        // The default caller is a log line or a debugger window, where a container running under
        // a comma-decimal locale must not change what the record says.
        using CultureScope _ = new("de-DE");

        Assert.Equal(
            "1500.5 m Msl",
            Altitude.FromMeters(1500.5, AltitudeReference.Msl).ToString());
    }

    [Fact]
    public void ToStringWithNullProvider_UsesCurrentCulture_UnlikeToString()
    {
        // The documented asymmetry, asserted as a contrast because the contrast is the point:
        // the IFormattable overload follows the framework convention (null provider means current
        // culture) while the parameterless overload is deliberately invariant. Display code picks
        // a culture on purpose; logs take ToString().
        using CultureScope _ = new("de-DE");

        Altitude altitude = Altitude.FromMeters(1500.5, AltitudeReference.Msl);

        Assert.Equal("1500,5 m Msl", altitude.ToString(null, null));
        Assert.Equal("1500.5 m Msl", altitude.ToString());
    }

    [Fact]
    public void ToString_UnitAndReference_AreNeverLocalised()
    {
        using CultureScope _ = new("ar-SA");

        string formatted = Altitude.FromMeters(1500.5, AltitudeReference.Agl).ToString(null, null);

        Assert.Contains(" m ", formatted, StringComparison.Ordinal);
        Assert.Contains("Agl", formatted, StringComparison.Ordinal);
    }
}
