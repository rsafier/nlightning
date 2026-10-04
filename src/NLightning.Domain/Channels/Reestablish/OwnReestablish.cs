namespace NLightning.Domain.Channels.Reestablish;

/// <summary>The numbers of our <c>channel_reestablish</c>.</summary>
/// <param name="NextCommitmentNumber">L + 1.</param>
/// <param name="NextRevocationNumber">R.</param>
/// <param name="LastReceivedSecretNumber">R - 1: the peer's commitment whose secret we send back, or null for zeroes.
/// </param>
/// <param name="CurrentPointNumber">L: the commitment whose per-commitment point we send.</param>
/// <param name="NextFunding">Our <c>next_funding</c> TLV (SP-RE-01), or null (splicing plan SP2-0).</param>
/// <param name="MyCurrentFundingLocked">Our <c>my_current_funding_locked</c> TLV (SP-RE-02), or null (splicing plan
/// SP2-0).</param>
public sealed record OwnReestablish(
    ulong NextCommitmentNumber,
    ulong NextRevocationNumber,
    ulong? LastReceivedSecretNumber,
    ulong CurrentPointNumber,
    ReestablishFundingField? NextFunding = null,
    ReestablishFundingField? MyCurrentFundingLocked = null);