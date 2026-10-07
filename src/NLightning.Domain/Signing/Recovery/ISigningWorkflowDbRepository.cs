namespace NLightning.Domain.Signing.Recovery;

using Channels.ValueObjects;

/// <summary>All mutations are staged on their owning unit of work.</summary>
public interface ISigningWorkflowDbRepository
{
    Task<SigningWorkflow?> GetAsync(Guid workflowId);
    Task<IReadOnlyList<SigningWorkflow>> GetPendingForChannelAsync(ChannelId channelId);
    Task<IReadOnlyList<SigningRequest>> GetRequestsAsync(Guid workflowId);
    Task AddWorkflowAsync(SigningWorkflow workflow);
    Task AddRequestAsync(SigningRequest request);
    Task UpdateWorkflowAsync(SigningWorkflow workflow);
    Task UpdateRequestAsync(SigningRequest request);
    Task ConsumeWorkflowAsync(Guid workflowId);
}