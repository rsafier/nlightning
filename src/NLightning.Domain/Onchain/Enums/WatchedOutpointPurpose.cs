namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// Why an outpoint is watched for a spend (persisted as a byte: never renumber).
/// </summary>
public enum WatchedOutpointPurpose : byte
{
    /// <summary>A channel's funding output: any spend closes the channel (mutual, unilateral or revoked).</summary>
    FundingOutput = 1,

    /// <summary>An output of a commitment or second-level transaction that we must resolve (BOLT 5).</summary>
    ResolutionOutput = 2
}