using System.Reflection;

namespace Mcs.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="VehicleId"/>.
/// </summary>
/// <remarks>
/// <see cref="VehicleId"/> is a smart constructor: <see cref="VehicleId.From"/> is the only way
/// in, so every invariant the rest of the station relies on is established here or nowhere. The
/// cases below are organised as equivalence classes (what the allowlist accepts and rejects),
/// boundary values around <see cref="VehicleId.MaxLength"/>, and the two properties the XML docs
/// assert but which no ordinary usage would reveal -- that a rejected character never reaches the
/// exception message, and that a <c>default</c> instance is describable but not readable.
/// <para>
/// Invisible and non-ASCII inputs are built from their code unit in the test body rather than
/// pasted into an <c>InlineData</c> literal, so a case cannot be silently altered by an editor,
/// a diff tool, or a copy-paste that normalises whitespace.
/// </para>
/// </remarks>
public class VehicleIdTests
{
    // --- From: accepted inputs -------------------------------------------------------------

    [Theory]
    [InlineData("UAV-01")]
    [InlineData("a")]
    [InlineData("9")]
    [InlineData("Z")]
    [InlineData("_")]
    [InlineData("-")]
    [InlineData("UAV_01-alpha")]
    [InlineData("____")]
    [InlineData("------")]
    [InlineData("0123456789")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("MiXeD-CaSe_99")]
    public void From_AllowedShape_RoundTripsUnchanged(string raw) =>
        Assert.Equal(raw, VehicleId.From(raw).Value);

    [Theory]
    [InlineData("  UAV-01  ")]
    [InlineData("UAV-01 ")]
    [InlineData(" UAV-01")]
    [InlineData("\tUAV-01\n")]
    [InlineData("\r\n UAV-01 \r\n")]
    public void From_SurroundingWhitespace_IsTrimmed(string padded) =>
        Assert.Equal("UAV-01", VehicleId.From(padded).Value);

    [Theory]
    [InlineData(0x00A0)] // NBSP
    [InlineData(0x2003)] // EM SPACE
    [InlineData(0x3000)] // IDEOGRAPHIC SPACE
    public void From_UnicodeWhitespacePadding_IsTrimmed(int codeUnit)
    {
        // Trim() uses char.IsWhiteSpace, which is broader than ASCII space -- so these pad the
        // same way a plain space does, and must normalise away the same way.
        char ws = (char)codeUnit;

        Assert.Equal("UAV-01", VehicleId.From($"{ws}UAV-01{ws}").Value);
    }

    [Fact]
    public void From_PaddedAndUnpadded_ProduceEqualIds()
    {
        // The documented asymmetry: padding is an artefact of a CSV column or a query string and
        // is never meaningful, so it is normalised away rather than producing a third track.
        VehicleId padded = VehicleId.From("  UAV-01  ");
        VehicleId bare = VehicleId.From("UAV-01");

        Assert.Equal(bare, padded);
        Assert.Equal(bare.GetHashCode(), padded.GetHashCode());
    }

    // --- From: boundary-value analysis on MaxLength -----------------------------------------

    [Fact]
    public void MaxLength_IsSixtyFour()
    {
        // Pinned so widening the cap is a deliberate act. The constant is public precisely so the
        // ingest boundary and any UI-side check state the same limit instead of drifting apart.
        Assert.Equal(64, VehicleId.MaxLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    public void From_LengthUpToMax_IsAccepted(int length)
    {
        string raw = new('x', length);

        Assert.Equal(raw, VehicleId.From(raw).Value);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(100)]
    [InlineData(4096)]
    public void From_LengthOverMax_ThrowsArgumentOutOfRange(int length)
    {
        string raw = new('x', length);

        ArgumentOutOfRangeException ex =
            Assert.Throws<ArgumentOutOfRangeException>(() => VehicleId.From(raw));

        Assert.Equal("value", ex.ParamName);
        Assert.Equal(length, Assert.IsType<int>(ex.ActualValue));
    }

    [Fact]
    public void From_MaxLengthIdWrappedInWhitespace_IsAccepted()
    {
        // Trimming happens before the length check, so the cap judges the string that will
        // actually be stored rather than its transport packaging.
        string bare = new('x', VehicleId.MaxLength);

        Assert.Equal(bare, VehicleId.From($"   {bare}   ").Value);
    }

    [Fact]
    public void From_OverLongIdWrappedInWhitespace_ReportsTrimmedLength()
    {
        string bare = new('x', VehicleId.MaxLength + 1);

        ArgumentOutOfRangeException ex =
            Assert.Throws<ArgumentOutOfRangeException>(() => VehicleId.From($"   {bare}   "));

        // 65, not 71 -- the padding is not part of what the cap is bounding.
        Assert.Equal(VehicleId.MaxLength + 1, Assert.IsType<int>(ex.ActualValue));
    }

    // --- From: rejected inputs ---------------------------------------------------------------

    [Fact]
    public void From_Null_ThrowsArgumentNullException()
    {
        // ArgumentNullException derives from ArgumentException, and xUnit's Assert.Throws matches
        // the exact type -- so this genuinely pins which of the two the caller will catch.
        ArgumentNullException ex =
            Assert.Throws<ArgumentNullException>(() => VehicleId.From(null!));

        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" \t\r\n ")]
    public void From_EmptyOrWhitespace_ThrowsArgumentException(string raw)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => VehicleId.From(raw));

        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData("UAV 01", "U+0020", 3)]   // interior space
    [InlineData("UAV.01", "U+002E", 3)]   // path-traversal fodder
    [InlineData("UAV/01", "U+002F", 3)]   // would split a URL path segment
    [InlineData("UAV\\01", "U+005C", 3)]
    [InlineData("UAV:01", "U+003A", 3)]
    [InlineData("UAV\"01", "U+0022", 3)]  // would break out of a JSON string
    [InlineData("UAV<01", "U+003C", 3)]   // would break out of an HTML context
    [InlineData("UAV>01", "U+003E", 3)]
    [InlineData("UAV&01", "U+0026", 3)]
    [InlineData("UAV%01", "U+0025", 3)]
    [InlineData("UAV+01", "U+002B", 3)]
    [InlineData("UAV*01", "U+002A", 3)]
    [InlineData("UAV'01", "U+0027", 3)]
    [InlineData("UAV;01", "U+003B", 3)]
    [InlineData("UAV=01", "U+003D", 3)]
    [InlineData("UAV?01", "U+003F", 3)]
    [InlineData("UAV#01", "U+0023", 3)]
    [InlineData("$UAV", "U+0024", 0)]     // rejected at the first character
    public void From_DisallowedAsciiCharacter_ThrowsArgumentExceptionNamingTheCodePoint(
        string raw, string expectedCodePoint, int expectedIndex)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => VehicleId.From(raw));
        string authored = AuthoredMessage(ex);

        Assert.Equal("value", ex.ParamName);
        Assert.Contains(expectedCodePoint, authored, StringComparison.Ordinal);
        Assert.Contains($"at index {expectedIndex}", authored, StringComparison.Ordinal);
    }

    // --- From: validation precedence ---------------------------------------------------------

    [Fact]
    public void From_OverLongAndAlsoIllegal_ReportsLengthFirst()
    {
        // The length check runs before the allowlist loop. Pinned because the two throw different
        // exception types, and a caller's catch blocks depend on which one arrives.
        string raw = new string('x', 32) + " " + new string('x', 32);

        Assert.Throws<ArgumentOutOfRangeException>(() => VehicleId.From(raw));
    }

    [Fact]
    public void From_LongWhitespaceOnly_ReportsEmptinessFirst()
    {
        // ThrowIfNullOrWhiteSpace runs before the length check, so this is ArgumentException even
        // though the untrimmed string is far over the cap.
        string raw = new(' ', 100);

        Assert.Throws<ArgumentException>(() => VehicleId.From(raw));
    }

    // --- From: the log-injection defence -----------------------------------------------------

    [Theory]
    [InlineData(0x0000, "U+0000")] // NUL
    [InlineData(0x0009, "U+0009")] // TAB (interior, so it survives Trim)
    [InlineData(0x000A, "U+000A")] // LF -- would split a log record in two
    [InlineData(0x000D, "U+000D")] // CR
    [InlineData(0x001B, "U+001B")] // ESC -- ANSI escape sequence injection into a terminal
    [InlineData(0x007F, "U+007F")] // DEL
    [InlineData(0x0085, "U+0085")] // NEL -- a line break to some log readers
    [InlineData(0x00A0, "U+00A0")] // NBSP (interior; only surrounding whitespace is trimmed)
    [InlineData(0x00DC, "U+00DC")] // U-umlaut: a letter, but not an ASCII one
    [InlineData(0x0410, "U+0410")] // Cyrillic capital A
    [InlineData(0x200B, "U+200B")] // ZERO WIDTH SPACE -- invisible in every log viewer
    [InlineData(0x202E, "U+202E")] // RIGHT-TO-LEFT OVERRIDE -- reorders text after it
    public void From_InvisibleOrNonAsciiCharacter_MessageNamesCodePointAndNeverEchoesIt(
        int codeUnit, string expectedCodePoint)
    {
        char rejected = (char)codeUnit;
        ArgumentException ex =
            Assert.Throws<ArgumentException>(() => VehicleId.From($"UAV{rejected}01"));
        string authored = AuthoredMessage(ex);

        Assert.Equal("value", ex.ParamName);
        Assert.Contains(expectedCodePoint, authored, StringComparison.Ordinal);
        Assert.Contains("at index 3", authored, StringComparison.Ordinal);

        // The property that matters: the rejected character itself must not survive into the
        // message, because that message is what gets logged when the rejection is recorded --
        // which would reintroduce, in the diagnostic, exactly what the allowlist keeps out.
        //
        // Only asserted for this theory, not the ASCII one above: the static part of the message
        // legitimately contains characters like '.', ';' and '\'', so a blanket "the rejected
        // character is absent" check would fail there for reasons that are not a defect.
        Assert.DoesNotContain(rejected.ToString(), authored, StringComparison.Ordinal);
        Assert.DoesNotContain(authored, char.IsControl);
    }

    [Fact]
    public void From_DisallowedCharacterInPaddedInput_ReportsIndexInTrimmedString()
    {
        // Index 3 of "UAV 01", not index 5 of "  UAV 01  ". The reported position has to be
        // usable against the value the exception is talking about.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => VehicleId.From("  UAV 01  "));

        Assert.Contains("at index 3", AuthoredMessage(ex), StringComparison.Ordinal);
    }

    // --- From: robustness against non-BMP input ----------------------------------------------

    [Fact]
    public void From_SurrogatePair_ThrowsArgumentExceptionRatherThanCrashing()
    {
        // A helicopter emoji is two UTF-16 code units. The validation loop walks code units, so
        // this proves it handles non-BMP input cleanly -- no IndexOutOfRangeException, no crash --
        // and reports the high surrogate it actually rejected.
        ArgumentException ex =
            Assert.Throws<ArgumentException>(() => VehicleId.From("UAV-\U0001F681"));

        Assert.Equal("value", ex.ParamName);
        Assert.Contains("U+D83D", AuthoredMessage(ex), StringComparison.Ordinal);
        Assert.Contains("at index 4", AuthoredMessage(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void From_LoneSurrogate_ThrowsArgumentExceptionRatherThanCrashing()
    {
        // An unpaired surrogate is not valid UTF-16 at all -- the kind of thing a truncated
        // network read produces. It must be rejected as a character, not crash the loop.
        string raw = "UAV-" + (char)0xD83D;

        Assert.Throws<ArgumentException>(() => VehicleId.From(raw));
    }

    // --- Value and ToString ------------------------------------------------------------------

    [Fact]
    public void Value_OnValidId_ReturnsTrimmedString() =>
        Assert.Equal("UAV-01", VehicleId.From("  UAV-01  ").Value);

    [Fact]
    public void ToString_OnValidId_ReturnsTheId() =>
        Assert.Equal("UAV-01", VehicleId.From("UAV-01").ToString());

    [Fact]
    public void Default_CanBeConstructedWithoutThrowing()
    {
        // The language will not let a struct refuse `default`. The type's answer is to fail on
        // read instead -- so construction itself must stay quiet.
        VehicleId uninitialised = default;

        Assert.Equal("VehicleId(uninitialised)", uninitialised.ToString());
    }

    [Fact]
    public void Value_OnDefault_ThrowsInvalidOperationException()
    {
        VehicleId uninitialised = default;

        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => { _ = uninitialised.Value; });

        Assert.Contains("VehicleId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_OnDefault_ReturnsSentinelAndDoesNotThrow()
    {
        // ToString deliberately does not route through Value. A throwing ToString turns a logged
        // bad id into an exception raised by the log statement itself, which inside a catch block
        // would replace the original exception.
        VehicleId uninitialised = default;

        Assert.Equal("VehicleId(uninitialised)", uninitialised.ToString());
    }

    [Fact]
    public void Interpolation_OnDefault_ReturnsSentinelAndDoesNotThrow()
    {
        // The realistic log-template path: interpolation goes through ToString, not Value.
        VehicleId uninitialised = default;

        Assert.Equal("VehicleId(uninitialised)", $"{uninitialised}");
    }

    // --- Equality, hashing, and use as a store key -------------------------------------------

    [Fact]
    public void Equality_IsCaseSensitive()
    {
        // Documented and intentional: "uav-01" and "UAV-01" are two different vehicles. A MAVLink
        // adapter deriving ids from system IDs has to normalise casing at its own boundary, or one
        // vehicle renders as two tracks.
        Assert.NotEqual(VehicleId.From("UAV-01"), VehicleId.From("uav-01"));
    }

    [Fact]
    public void Equality_DistinctIds_AreNotEqual() =>
        Assert.NotEqual(VehicleId.From("UAV-01"), VehicleId.From("UAV-02"));

    [Fact]
    public void Equality_SameId_HasSameHashCode()
    {
        VehicleId a = VehicleId.From("UAV-01");
        VehicleId b = VehicleId.From("UAV-01");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DefaultInstances_AreEqual()
    {
        VehicleId a = default;
        VehicleId b = default;

        // Equality reads the backing field, not the guarded Value property, so this does not
        // throw. Characterisation: worth knowing before the store starts comparing ids.
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DefaultAndValid_AreNotEqual()
    {
        VehicleId uninitialised = default;

        Assert.NotEqual(VehicleId.From("UAV-01"), uninitialised);
    }

    [Fact]
    public void DictionaryKey_PaddedVariantsCollapse_ButCasingDoesNot()
    {
        // This is the behaviour the store's bounded vehicle count will depend on: padding must
        // not be able to manufacture extra vehicle slots, but casing legitimately can.
        Dictionary<VehicleId, int> store = new()
        {
            [VehicleId.From("UAV-01")] = 1,
        };

        store[VehicleId.From("  UAV-01  ")] = 2;
        Assert.Single(store);
        Assert.Equal(2, store[VehicleId.From("UAV-01")]);

        store[VehicleId.From("uav-01")] = 3;
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void DictionaryKey_Default_IsUsableWithoutThrowing()
    {
        // The source comment claims a default id can still be used as a dictionary key without
        // touching Value -- which is exactly why the store's write path must validate as well,
        // rather than assuming the type has already closed the hole.
        Dictionary<VehicleId, int> store = new() { [default] = 1 };

        Assert.True(store.ContainsKey(default));
        Assert.Equal(1, store[default]);
    }

    // --- Structural invariants ---------------------------------------------------------------

    [Fact]
    public void Type_IsValueTypeImplementingIEquatable()
    {
        // Backs the doc's "keys the telemetry store without boxing": a value type that implements
        // IEquatable<T> is what makes Dictionary<VehicleId, _> pick the non-boxing comparer.
        Assert.True(typeof(VehicleId).IsValueType);
        Assert.True(typeof(VehicleId).IsAssignableTo(typeof(IEquatable<VehicleId>)));
    }

    [Fact]
    public void Type_AllInstanceFields_AreReadOnly()
    {
        // Backs the immutability the rest of the station assumes: an id crosses threads between
        // the feed and the SSE readers with no lock anywhere.
        FieldInfo[] fields = typeof(VehicleId)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.True(f.IsInitOnly, $"{f.Name} is not readonly."));
    }

    [Fact]
    public void Type_ExposesNoPublicConstructor()
    {
        // From() is the only way in. A public constructor appearing here would mean validation
        // had become bypassable -- the one thing the smart-constructor pattern exists to prevent.
        Assert.Empty(typeof(VehicleId).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    /// <summary>
    /// Strips the "(Parameter 'name')" suffix that <see cref="ArgumentException"/> appends, so
    /// assertions test the message the implementation authored rather than what the framework
    /// added. Necessary because that suffix is preceded by <see cref="Environment.NewLine"/> --
    /// which would defeat any assertion about control characters not appearing in the message.
    /// </summary>
    private static string AuthoredMessage(ArgumentException ex)
    {
        int suffix = ex.Message.IndexOf(
            Environment.NewLine + "(Parameter", StringComparison.Ordinal);

        return suffix < 0 ? ex.Message : ex.Message[..suffix];
    }
}

/// <summary>
/// <see cref="VehicleId"/> tests that mutate <see cref="System.Globalization.CultureInfo.CurrentCulture"/>.
/// </summary>
[Collection(CultureCollection.Name)]
public class VehicleIdCultureTests
{
    [Fact]
    public void Equality_IsOrdinal_EvenUnderTurkishCulture()
    {
        // Turkish is the standard trap: culture-aware casing maps 'I' and 'i' to different
        // letters than English does. Record-struct equality on a string field is ordinal, so
        // ambient culture must not be able to reach it. Pinned against a future regression that
        // swaps in a culture-sensitive comparison.
        using CultureScope _ = new("tr-TR");

        Assert.NotEqual(VehicleId.From("UAV-I"), VehicleId.From("UAV-i"));
        Assert.Equal(VehicleId.From("UAV-I"), VehicleId.From("UAV-I"));
    }
}
