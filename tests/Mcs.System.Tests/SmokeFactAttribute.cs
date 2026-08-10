namespace Mcs.System.Tests;

/// <summary>
/// A fact that skips itself when no stack is running, unless <c>MCS_SMOKE_REQUIRED</c> says
/// otherwise.
/// </summary>
/// <remarks>
/// This exists because xunit 2.9 cannot skip at runtime -- <c>Assert.Skip</c>, <c>SkipWhen</c> and
/// <c>SkipUnless</c> are all v3. Setting <see cref="FactAttribute.Skip"/> from the constructor is
/// the v2 way, and it costs nothing: the probe behind <see cref="SmokeStack.SkipReason"/> is
/// evaluated once per discovery process however many tests are decorated with this.
/// <para>
/// The alternative -- letting the tests run and return early when the stack is down -- was
/// rejected. That reports a pass for a test that asserted nothing, which is indistinguishable from
/// success in every report anyone reads. A skip is at least visible as an absence.
/// </para>
/// <para>
/// The consequence of deciding at discovery is that a stack which comes up between discovery and
/// execution still skips. That is the right way round: CI brings the stack up first and sets
/// <c>MCS_SMOKE_REQUIRED</c>, so the case cannot arise where it matters.
/// </para>
/// </remarks>
public sealed class SmokeFactAttribute : FactAttribute
{
    public SmokeFactAttribute() => Skip = SmokeStack.SkipReason;
}
