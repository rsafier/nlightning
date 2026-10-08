namespace NLightning.Domain.Signing.Recovery;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Persistence.Interfaces;

public interface IRemoteSigningWorkflowCoordinator
{
    Task<ISigningWorkflowScope> BeginAsync(SigningWorkflowDescriptor descriptor);
    Task StageAsync(SigningWorkflowDescriptor descriptor, IUnitOfWork unitOfWork);
    Task<IReadOnlyList<SigningWorkflow>> GetPendingAsync(ChannelId channelId);
}

public interface ISigningWorkflowScope : IDisposable
{
    Guid WorkflowId { get; }
    /// <summary>Called synchronously by the owner after BeginAsync to establish its ambient request capture.</summary>
    void Activate();
    Task StageConsumeAsync(IUnitOfWork unitOfWork);
}

public enum RemoteSigningRequestOutcome { Completed = 1, NotFound = 2, Unknown = 3, Invalidated = 4, Unsupported = 5 }
public sealed record RemoteSigningRequestStatus(RemoteSigningRequestOutcome Outcome, byte[]? Response = null);

/// <summary>Wire formats remain opaque to the application and Domain.</summary>
public interface IRemoteSigningRequestCapture
{
    byte[] Execute(uint operation, byte[] proposedEnvelope, byte[] argumentFingerprint,
        Func<byte[], RemoteSigningRequestStatus> reconcile, Func<byte[], byte[]> execute);
}

/// <summary>Replays saved native funding requests without reconstructing wallet inputs or replacing request identities.</summary>
public interface INativeFundingSigningRecovery
{
    SignedTransaction ReplayFunding(ISigningWorkflowScope workflow);
}

/// <summary>Replays the original reserved-input wallet signing envelope and exact receipt.</summary>
public interface INativeWalletSigningRecovery
{
    SignedTransaction? ReplayWithdrawal(ISigningWorkflowScope workflow);
}

/// <summary>Captures immutable sweep contexts and replays their exact native signing requests.</summary>
public interface INativeSweepSigningRecovery
{
    Task StageRetireSweepAsync(SigningWorkflowDescriptor descriptor, IUnitOfWork unitOfWork);
    byte[] EncodeSweepContexts(IReadOnlyList<Onchain.Models.SweepSigningContext> contexts);
    IReadOnlyList<Crypto.ValueObjects.CompactSignature> SignSweepInputs(ISigningWorkflowScope workflow, Bitcoin.Interfaces.ILightningSigner signer);
}

/// <summary>Initial delayed-sweep data includes the exact unsigned transaction and witness prerequisites.</summary>
public sealed record InitialDelayedSweepIntent(Onchain.Models.UnsignedSweepTransaction Transaction,
    Onchain.Models.OutputResolutionModel Output, Onchain.Models.ChannelCloseModel Close,
    uint Height, uint FeeratePerKw, Channels.ValueObjects.ChannelSigningInfo SigningInfo, bool OutputWasPersisted)
{
    public InitialDelayedSweepParent? Parent { get; init; }
}

/// <summary>The confirmed HTLC transaction and original commitment output that created a delayed sweep input.</summary>
public sealed record InitialDelayedSweepParent(Onchain.Models.OutputResolutionModel Output,
    uint ConfirmedHeight, Crypto.ValueObjects.Hash ConfirmedBlockHash);

public interface INativeInitialSweepSigningRecovery : INativeSweepSigningRecovery
{
    byte[] EncodeInitialSweepIntent(InitialDelayedSweepIntent intent);
    InitialDelayedSweepIntent DecodeInitialSweepIntent(byte[] encoded);
}

public sealed record ChannelKeyAllocation(uint KeyIndex, Channels.ValueObjects.ChannelBasepoints Basepoints,
    Crypto.ValueObjects.CompactPubKey FirstPerCommitmentPoint);

public interface INativeChannelKeyAllocationRecovery
{
    ChannelKeyAllocation AllocateChannelKeys(ISigningWorkflowScope workflow, Bitcoin.Interfaces.ILightningSigner signer);
}