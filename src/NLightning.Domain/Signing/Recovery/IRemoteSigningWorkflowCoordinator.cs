namespace NLightning.Domain.Signing.Recovery;

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