/// <summary>
///   Names the requirement in <c>docs/requirements.md</c> that a test exists to verify.
/// </summary>
/// <remarks>
///   <para>
///     The tag is read back by <c>tools/trace</c>, which pairs it with the test's reported
///     outcome and fails the build when a row of the requirements table claims evidence the
///     tests no longer supply. Nothing at runtime looks at it: xUnit never sees it, and it
///     deliberately is not an xUnit <c>[Trait]</c> — traits reach the VSTest object model but
///     the TRX writer drops every one that MSTest did not put there, so a trait would be
///     invisible to exactly the tool that needs it.
///   </para>
///   <para>
///     <see cref="AttributeTargets.Class"/> as well as <see cref="AttributeTargets.Method"/>,
///     and <c>AllowMultiple</c>, because the mapping runs both ways: one requirement is usually
///     covered by several tests, one test sometimes covers several requirements, and a class
///     whose every method bears on the same requirement is more honestly tagged once than
///     thirty times. The rule for choosing is that the tag goes where
///     <c>docs/requirements.md</c> names the evidence — that correspondence is the thing being
///     mechanised, and a tag placed more broadly than the prose claims quietly widens it.
///   </para>
///   <para>
///     This file is <c>&lt;Compile Include&gt;</c>-linked into the suites that use it rather
///     than copied into each, which is the opposite of the call made for <c>FakeClock</c>. Two
///     copies of a clock may drift apart harmlessly; two copies of this may not, because the
///     trace tool matches the attribute <em>by name</em> and a renamed copy tags nothing and
///     says nothing about it. Linking also keeps <c>Mcs.System.Tests</c>' rule intact — it
///     still has no <c>ProjectReference</c>, so nothing about the wire contract can arrive
///     this way.
///   </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
internal sealed class VerifiesAttribute(string requirementId) : Attribute
{
    /// <summary>The requirement's identifier, in the form <c>MCS-001</c>.</summary>
    public string RequirementId { get; } = requirementId;
}
