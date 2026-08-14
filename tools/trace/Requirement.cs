namespace Mcs.Trace;

/// <summary>How a requirement claims to be verified.</summary>
internal enum VerificationMethod
{
    Test,
    Inspection,
    Demonstration,
    Analysis,
}

/// <summary>What the table's Status column claims about a requirement today.</summary>
/// <remarks>
///   <para>
///     <see cref="NotVerified"/> is a <em>pass</em>, and designing it in from the start is what
///     stops this whole mechanism from becoming an incentive to delete requirements: a table that
///     can only be made green by removing rows will be made green by removing rows. It is
///     distinguishable from missing evidence because the row says it out loud and gives a reason.
///   </para>
///   <para>
///     <see cref="PartlyVerified"/> is held to the same standard as <see cref="Verified"/> and
///     additionally owes a reason. It exists for a row whose method is genuinely satisfied for
///     part of the claim -- MCS-013 tests the rendering and not the detection -- and the point of
///     giving it its own state rather than rounding it to either neighbour is that rounding down
///     hides working evidence and rounding up hides the gap.
///   </para>
/// </remarks>
internal enum VerificationStatus
{
    Verified,
    PartlyVerified,
    NotVerified,
}

/// <summary>An <c>evidence:</c> marker found in a requirement's section, and where it pointed.</summary>
internal sealed record EvidenceLink(string Target, int Line);

/// <summary>One row of the requirements table, joined to the section beneath it.</summary>
internal sealed record Requirement(
    string Id,
    int Number,
    string Summary,
    IReadOnlyList<VerificationMethod> Methods,
    VerificationStatus Status,
    string? StatusReason,
    IReadOnlyList<EvidenceLink> Evidence,
    int Line);
