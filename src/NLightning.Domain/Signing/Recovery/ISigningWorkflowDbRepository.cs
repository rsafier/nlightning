namespace NLightning.Domain.Signing.Recovery;

using Channels.ValueObjects;

/// <summary>All mutations are staged on their owning unit of work.</summary>
public interface ISigningWorkflowDbRepository
{
    Task<SigningWorkflow?> GetAsync(Guid workflowId);
    Task<SigningWorkflow?> GetLatestForChannelAsync(ChannelId channelId, SigningWorkflowKind kind) =>
        throw new NotSupportedException("This repository cannot read retained signing lifecycles.");
    Task<IReadOnlyList<SigningWorkflow>> GetPendingForChannelAsync(ChannelId channelId);
    Task<IReadOnlyList<SigningWorkflow>> GetUnconsumedAsync(SigningWorkflowKind kind) =>
        throw new NotSupportedException("This repository cannot discover unconsumed signing intents.");
    Task<IReadOnlyList<SigningRequest>> GetRequestsAsync(Guid workflowId);
    Task AddWorkflowAsync(SigningWorkflow workflow);
    Task AddRequestAsync(SigningRequest request);
    Task UpdateWorkflowAsync(SigningWorkflow workflow);
    Task UpdateRequestAsync(SigningRequest request);
    Task ConsumeWorkflowAsync(Guid workflowId);
}