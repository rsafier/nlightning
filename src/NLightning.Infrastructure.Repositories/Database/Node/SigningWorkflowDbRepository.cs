using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Node;

using Domain.Channels.ValueObjects;
using Domain.Signing.Recovery;
using Persistence.Contexts;
using Persistence.Entities.Node;

/// <summary>Staged signing intent/result writes; receipts are never evicted or assigned another identity.</summary>
public sealed class SigningWorkflowDbRepository(NLightningDbContext context) : ISigningWorkflowDbRepository
{
    private const int MaxWorkflows = 65_536;
    private const int MaxRequests = 262_144;
    private const int MaxEnvelopeBytes = 1_048_576;
    private const int MaxResponseBytes = 4_194_304;

    public async Task<SigningWorkflow?> GetAsync(Guid workflowId)
    {
        var entity = await context.SigningWorkflows.AsNoTracking()
                                  .SingleOrDefaultAsync(e => e.WorkflowId == workflowId);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<SigningWorkflow>> GetPendingForChannelAsync(ChannelId channelId)
    {
        var entities = await context.SigningWorkflows.AsNoTracking()
                                    .Where(e => e.ChannelId.Equals(channelId)
                                             && e.State != (int)SigningWorkflowState.Consumed)
                                    .OrderBy(e => e.CreatedAtTicks).ToListAsync();
        // A holder-validation workflow can be consumed and its revocation intent staged in one save.
        // Overlay tracked lifecycle changes before checking the channel's pending ownership.
        var tracked = context.ChangeTracker.Entries<SigningWorkflowEntity>()
                             .Where(e => e.State != EntityState.Deleted && e.Entity.ChannelId == channelId)
                             .Select(e => e.Entity).ToDictionary(e => e.WorkflowId);
        var merged = entities.Select(e => tracked.TryGetValue(e.WorkflowId, out var current) ? current : e)
                             .Concat(tracked.Values.Where(e => entities.All(saved => saved.WorkflowId != e.WorkflowId)));
        return merged.Where(e => e.State != (int)SigningWorkflowState.Consumed).Select(Map).ToList();
    }

    public async Task<IReadOnlyList<SigningRequest>> GetRequestsAsync(Guid workflowId)
    {
        var entities = await context.SigningRequests.AsNoTracking().Where(e => e.WorkflowId == workflowId)
                                    .OrderBy(e => e.Ordinal).ToListAsync();
        return entities.Select(Map).ToList();
    }

    public async Task AddWorkflowAsync(SigningWorkflow workflow)
    {
        Validate(workflow);
        if (workflow.State != SigningWorkflowState.Pending)
            throw new InvalidOperationException("A new signing workflow must be pending.");
        if (await context.SigningWorkflows.CountAsync()
          + context.ChangeTracker.Entries<SigningWorkflowEntity>().Count(e => e.State == EntityState.Added) >= MaxWorkflows)
            throw new InvalidOperationException("Signing workflow receipt capacity exhausted.");
        context.SigningWorkflows.Add(new SigningWorkflowEntity
        {
            WorkflowId = workflow.WorkflowId,
            ChannelId = workflow.ChannelId,
            ActiveChannelId = workflow.ChannelId,
            Kind = (int)workflow.Kind,
            ExpectedLocalCommitmentNumber = workflow.ExpectedLocalCommitmentNumber,
            ExpectedRemoteCommitmentNumber = workflow.ExpectedRemoteCommitmentNumber,
            SnapshotFingerprint = workflow.SnapshotFingerprint.ToArray(),
            SignerIdentity = workflow.SignerIdentity.ToArray(),
            Network = workflow.Network,
            SchemaVersion = workflow.SchemaVersion,
            State = (int)workflow.State,
            CreatedAtTicks = workflow.CreatedAtTicks,
            UpdatedAtTicks = workflow.UpdatedAtTicks
        });
    }

    public async Task AddRequestAsync(SigningRequest request)
    {
        Validate(request);
        if (request.State != SigningRequestState.Prepared || request.Response is not null)
            throw new InvalidOperationException("A new signing request must be prepared without a response.");
        var workflow = await RequireWorkflowAsync(request.WorkflowId);
        if (workflow.State != (int)SigningWorkflowState.Pending)
            throw new InvalidOperationException("Cannot extend a non-pending signing workflow.");
        if (await context.SigningRequests.CountAsync()
          + context.ChangeTracker.Entries<SigningRequestEntity>().Count(e => e.State == EntityState.Added) >= MaxRequests)
            throw new InvalidOperationException("Signing request receipt capacity exhausted.");
        var count = await context.SigningRequests.CountAsync(e => e.WorkflowId == request.WorkflowId)
                  + context.ChangeTracker.Entries<SigningRequestEntity>()
                           .Count(e => e.State == EntityState.Added && e.Entity.WorkflowId == request.WorkflowId);
        if (request.Ordinal != count)
            throw new InvalidOperationException("Signing request ordinals must be contiguous.");
        context.SigningRequests.Add(new SigningRequestEntity
        {
            RequestId = request.RequestId,
            WorkflowId = request.WorkflowId,
            Ordinal = request.Ordinal,
            Operation = request.Operation,
            Envelope = request.Envelope.ToArray(),
            ArgumentFingerprint = request.ArgumentFingerprint.ToArray(),
            State = (int)request.State,
            Response = null,
            CreatedAtTicks = request.CreatedAtTicks,
            UpdatedAtTicks = request.UpdatedAtTicks
        });
    }

    public async Task UpdateWorkflowAsync(SigningWorkflow workflow)
    {
        Validate(workflow);
        var entity = await RequireWorkflowAsync(workflow.WorkflowId);
        if (!SameIdentity(Map(entity), workflow))
            throw new InvalidOperationException("Signing workflow prerequisites are immutable.");
        if (entity.State == (int)SigningWorkflowState.Consumed && workflow.State != SigningWorkflowState.Consumed
         || entity.State == (int)SigningWorkflowState.Blocked && workflow.State != SigningWorkflowState.Blocked)
            throw new InvalidOperationException("Signing workflow state cannot move backward.");
        if (workflow.State == SigningWorkflowState.Consumed)
            throw new InvalidOperationException("Consume workflows through ConsumeWorkflowAsync.");
        entity.State = (int)workflow.State;
        entity.UpdatedAtTicks = workflow.UpdatedAtTicks;
    }

    public async Task UpdateRequestAsync(SigningRequest request)
    {
        Validate(request);
        var entity = await context.SigningRequests.FindAsync(request.RequestId)
                  ?? throw new KeyNotFoundException("Signing request not found.");
        if (entity.WorkflowId != request.WorkflowId || entity.Ordinal != request.Ordinal
         || entity.Operation != request.Operation || entity.CreatedAtTicks != request.CreatedAtTicks
         || !entity.Envelope.AsSpan().SequenceEqual(request.Envelope)
         || !entity.ArgumentFingerprint.AsSpan().SequenceEqual(request.ArgumentFingerprint))
            throw new InvalidOperationException("Signing request identity is immutable.");
        if (entity.Response is not null && (request.Response is null
                                        || !entity.Response.AsSpan().SequenceEqual(request.Response)))
            throw new InvalidOperationException("Signing request responses are immutable.");
        if (entity.State == (int)SigningRequestState.Consumed || entity.State == (int)SigningRequestState.Blocked)
            throw new InvalidOperationException("Signing request is terminal.");
        if (request.State is not (SigningRequestState.Completed or SigningRequestState.Blocked))
            throw new InvalidOperationException("Invalid signing request state transition.");
        if (request.State == SigningRequestState.Completed && request.Response is null)
            throw new InvalidOperationException("A completed signing request needs its exact response.");
        entity.Response = request.Response?.ToArray();
        entity.State = (int)request.State;
        entity.UpdatedAtTicks = request.UpdatedAtTicks;
    }

    public async Task ConsumeWorkflowAsync(Guid workflowId)
    {
        var workflow = await RequireWorkflowAsync(workflowId);
        if (workflow.State == (int)SigningWorkflowState.Consumed)
            return;
        if (workflow.State != (int)SigningWorkflowState.Pending)
            throw new InvalidOperationException("Blocked signing workflows cannot be consumed.");
        var requests = await context.SigningRequests.Where(e => e.WorkflowId == workflowId).ToListAsync();
        requests.AddRange(context.ChangeTracker.Entries<SigningRequestEntity>()
                                 .Where(e => e.State == EntityState.Added && e.Entity.WorkflowId == workflowId)
                                 .Select(e => e.Entity));
        if (requests.Any(e => e.State != (int)SigningRequestState.Completed))
            throw new InvalidOperationException("Cannot consume an unresolved signing request.");
        workflow.State = (int)SigningWorkflowState.Consumed;
        workflow.ActiveChannelId = null;
        foreach (var request in requests)
            request.State = (int)SigningRequestState.Consumed;
    }

    private async Task<SigningWorkflowEntity> RequireWorkflowAsync(Guid id) =>
        await context.SigningWorkflows.FindAsync(id)
     ?? throw new KeyNotFoundException("Signing workflow not found.");

    private static bool SameIdentity(SigningWorkflow left, SigningWorkflow right) =>
        left.ChannelId.Equals(right.ChannelId) && left.Kind == right.Kind
     && left.ExpectedLocalCommitmentNumber == right.ExpectedLocalCommitmentNumber
     && left.ExpectedRemoteCommitmentNumber == right.ExpectedRemoteCommitmentNumber
     && left.SnapshotFingerprint.AsSpan().SequenceEqual(right.SnapshotFingerprint)
     && left.SignerIdentity.AsSpan().SequenceEqual(right.SignerIdentity)
     && left.Network == right.Network && left.SchemaVersion == right.SchemaVersion
     && left.CreatedAtTicks == right.CreatedAtTicks;

    private static void Validate(SigningWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.WorkflowId == Guid.Empty || workflow.SnapshotFingerprint.Length != 32
         || workflow.SignerIdentity.Length != 33 || string.IsNullOrWhiteSpace(workflow.Network)
         || workflow.Network.Length > 64 || workflow.SchemaVersion < 1
         || !Enum.IsDefined(workflow.Kind) || !Enum.IsDefined(workflow.State))
            throw new ArgumentException("Invalid signing workflow.");
    }

    private static void Validate(SigningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.WorkflowId == Guid.Empty || request.Ordinal < 0
         || request.Envelope.Length is 0 or > MaxEnvelopeBytes || request.ArgumentFingerprint.Length != 32
         || request.Response?.Length > MaxResponseBytes || !Enum.IsDefined(request.State))
            throw new ArgumentException("Invalid signing request.");
    }

    private static SigningWorkflow Map(SigningWorkflowEntity entity) => new(entity.WorkflowId, entity.ChannelId,
        (SigningWorkflowKind)entity.Kind, entity.ExpectedLocalCommitmentNumber, entity.ExpectedRemoteCommitmentNumber,
        entity.SnapshotFingerprint.ToArray(), entity.SignerIdentity.ToArray(), entity.Network, entity.SchemaVersion,
        (SigningWorkflowState)entity.State, entity.CreatedAtTicks, entity.UpdatedAtTicks);

    private static SigningRequest Map(SigningRequestEntity entity) => new(entity.RequestId, entity.WorkflowId,
        entity.Ordinal, entity.Operation, entity.Envelope.ToArray(), entity.ArgumentFingerprint.ToArray(),
        (SigningRequestState)entity.State, entity.Response?.ToArray(), entity.CreatedAtTicks, entity.UpdatedAtTicks);
}