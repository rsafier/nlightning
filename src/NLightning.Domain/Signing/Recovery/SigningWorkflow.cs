namespace NLightning.Domain.Signing.Recovery;

using Channels.ValueObjects;

public enum SigningWorkflowKind
{
    SendCommit = 1, ReleaseRevoke = 2, ValidateHolder = 3, ValidatePeerRevoke = 4, Opening = 5, Activate = 6, Funding = 7, WalletWithdrawal = 8, OnchainSweep = 9, OnchainInitialSweep = 10, ChannelKeyAllocation = 11
}
public enum SigningWorkflowState { Pending = 1, Consumed = 2, Blocked = 3, Abandoned = 4 }
public enum SigningRequestState { Prepared = 1, Completed = 2, Consumed = 3, Blocked = 4 }

/// <summary>Application prerequisites bound to one signing lifecycle transition.</summary>
public sealed record SigningWorkflowDescriptor(ChannelId ChannelId, SigningWorkflowKind Kind,
    ulong ExpectedLocalCommitmentNumber, ulong ExpectedRemoteCommitmentNumber, byte[] SnapshotFingerprint)
{
    public byte[]? PublicationIntent { get; init; }
}

public sealed record SigningWorkflow(Guid WorkflowId, ChannelId ChannelId, SigningWorkflowKind Kind,
    ulong ExpectedLocalCommitmentNumber, ulong ExpectedRemoteCommitmentNumber, byte[] SnapshotFingerprint,
    byte[] SignerIdentity, string Network, int SchemaVersion, SigningWorkflowState State,
    long CreatedAtTicks, long UpdatedAtTicks)
{
    public byte[]? PublicationIntent { get; init; }
}

/// <summary>The exact opaque wire request and public response, never a replacement request ID.</summary>
public sealed record SigningRequest(Guid RequestId, Guid WorkflowId, int Ordinal, uint Operation,
    byte[] Envelope, byte[] ArgumentFingerprint, SigningRequestState State, byte[]? Response,
    long CreatedAtTicks, long UpdatedAtTicks);