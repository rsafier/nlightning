using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Domain.Signing.Recovery;
using StoredRequest = NLightning.Domain.Signing.Recovery.SigningRequest;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>Captures requests only inside validated application transitions; recovery never guesses a new request ID.</summary>
public sealed class VlsSigningWorkflowCoordinator : IRemoteSigningWorkflowCoordinator, IRemoteSigningRequestCapture, IDisposable
{
    public const int SchemaVersion = 2;
    public const int MaximumEnvelopeBytes = 1024 * 1024;
    public const int MaximumRequestsPerWorkflow = 256;
    private readonly VlsSignerConnection _connection;
    private readonly IServiceScopeFactory _scopes;
    private readonly AsyncLocal<WorkflowScope?> _active = new();
    private readonly bool _attached;
    private bool _disposed;
    private readonly ConcurrentDictionary<ChannelId, SemaphoreSlim> _channelGates = new();

    public VlsSigningWorkflowCoordinator(VlsSignerConnection connection, IServiceScopeFactory scopes, bool attachCapture = true)
    {
        _connection = connection;
        _scopes = scopes;
        _attached = attachCapture;
        if (attachCapture) connection.AttachWorkflowCapture(this);
    }

    public async Task<ISigningWorkflowScope> BeginAsync(SigningWorkflowDescriptor descriptor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDescriptor(descriptor);
        if (_active.Value is not null) throw Blocked("Signing workflows cannot be nested.");
        var gate = _channelGates.GetOrAdd(descriptor.ChannelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            using var scope = _scopes.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var pending = await uow.SigningWorkflowDbRepository.GetPendingForChannelAsync(descriptor.ChannelId);
            if (pending.Count > 1) throw Blocked("The channel has multiple pending signing workflows.");
            SigningWorkflow workflow;
            if (pending.Count == 1)
            {
                workflow = pending[0];
                ValidateWorkflow(workflow, descriptor);
            }
            else
            {
                workflow = CreateWorkflow(descriptor);
                await uow.SigningWorkflowDbRepository.AddWorkflowAsync(workflow);
                await uow.SaveChangesAsync();
            }
            var requests = await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId);
            ValidateRequests(workflow, requests);
            return new WorkflowScope(this, CloneWorkflow(workflow), requests.Select(CloneRequest).ToList(), gate);
        }
        catch { gate.Release(); throw; }
    }

    /// <summary>ReleaseRevoke intent is committed in the same node transaction as the incoming local commitment.</summary>
    public async Task StageAsync(SigningWorkflowDescriptor descriptor, IUnitOfWork unitOfWork)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDescriptor(descriptor);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (descriptor.Kind is not (SigningWorkflowKind.ReleaseRevoke or SigningWorkflowKind.Activate))
            throw Blocked("Only a post-save revocation release or activation can be staged with a channel transition.");
        var pending = await unitOfWork.SigningWorkflowDbRepository.GetPendingForChannelAsync(descriptor.ChannelId);
        if (pending.Count > 1) throw Blocked("The channel has multiple pending signing workflows.");
        if (pending.Count == 1) ValidateWorkflow(pending[0], descriptor);
        else await unitOfWork.SigningWorkflowDbRepository.AddWorkflowAsync(CreateWorkflow(descriptor));
    }

    public async Task<IReadOnlyList<SigningWorkflow>> GetPendingAsync(ChannelId channelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var scope = _scopes.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var pending = await uow.SigningWorkflowDbRepository.GetPendingForChannelAsync(channelId);
        return pending.Select(CloneWorkflow).ToArray();
    }

    public byte[] Execute(uint operation, byte[] proposedEnvelope, byte[] argumentFingerprint,
        Func<byte[], RemoteSigningRequestStatus> reconcile, Func<byte[], byte[]> execute)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var workflow = _active.Value;
        if (workflow is null) return execute(proposedEnvelope.ToArray());
        return workflow.Execute(operation, proposedEnvelope, argumentFingerprint, reconcile, execute);
    }

    private SigningWorkflow CreateWorkflow(SigningWorkflowDescriptor descriptor)
    {
        var ticks = DateTime.UtcNow.Ticks;
        return new SigningWorkflow(Guid.NewGuid(), descriptor.ChannelId, descriptor.Kind,
            descriptor.ExpectedLocalCommitmentNumber, descriptor.ExpectedRemoteCommitmentNumber,
            descriptor.SnapshotFingerprint.ToArray(), ((byte[])_connection.Identity.NodePublicKey).ToArray(),
            CanonicalNetwork(_connection.Identity.Network), SchemaVersion, SigningWorkflowState.Pending, ticks, ticks);
    }

    private void ValidateWorkflow(SigningWorkflow workflow, SigningWorkflowDescriptor descriptor)
    {
        if (workflow.State != SigningWorkflowState.Pending || workflow.SchemaVersion != SchemaVersion
         || !workflow.SignerIdentity.AsSpan().SequenceEqual((byte[])_connection.Identity.NodePublicKey)
         || !string.Equals(workflow.Network, CanonicalNetwork(_connection.Identity.Network), StringComparison.Ordinal)
         || workflow.ChannelId != descriptor.ChannelId || workflow.Kind != descriptor.Kind
         || workflow.ExpectedLocalCommitmentNumber != descriptor.ExpectedLocalCommitmentNumber
         || workflow.ExpectedRemoteCommitmentNumber != descriptor.ExpectedRemoteCommitmentNumber
         || !workflow.SnapshotFingerprint.AsSpan().SequenceEqual(descriptor.SnapshotFingerprint))
            throw Blocked("Pending signing workflow does not match the current channel snapshot, signer or network.");
    }
    private static void ValidateDescriptor(SigningWorkflowDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.SnapshotFingerprint is not { Length: 32 }
         || !Enum.IsDefined(descriptor.Kind))
            throw new ArgumentException("A signing workflow requires a supported kind and a 32-byte snapshot fingerprint.");
    }
    private static void ValidateRequests(SigningWorkflow workflow, IReadOnlyList<StoredRequest> requests)
    {
        if (requests.Count > MaximumRequestsPerWorkflow) throw Blocked("Signing workflow request limit exceeded.");
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (request.WorkflowId != workflow.WorkflowId || request.Ordinal != i
             || request.State is not (SigningRequestState.Prepared or SigningRequestState.Completed)
             || request.ArgumentFingerprint is not { Length: 32 }
             || request.Envelope.Length is <= 0 or > MaximumEnvelopeBytes
             || request.State == SigningRequestState.Completed && request.Response is null
             || !Allowed(workflow.Kind, request.Operation))
                throw Blocked("Pending signing workflow contains an invalid or unsupported request sequence.");
            ValidateEnvelope(request.Operation, request.Envelope, request.ArgumentFingerprint, request.RequestId);
        }
    }
    private static bool Allowed(SigningWorkflowKind kind, uint operation) => kind switch
    {
        SigningWorkflowKind.SendCommit => operation is VlsOperations.SignRemote or VlsOperations.PaymentPreimages,
        SigningWorkflowKind.ValidateHolder => operation is VlsOperations.ValidateHolder or VlsOperations.PaymentPreimages,
        SigningWorkflowKind.ValidatePeerRevoke => operation == VlsOperations.ValidateRevocation,
        SigningWorkflowKind.ReleaseRevoke => operation == VlsOperations.RevokeHolder,
        SigningWorkflowKind.Opening => operation is VlsOperations.Setup or VlsOperations.SignRemote
                                                 or VlsOperations.ValidateHolder,
        SigningWorkflowKind.Activate => operation == VlsOperations.Activate,
        _ => false
    };
    private static void ValidateEnvelope(uint operation, byte[] envelope, byte[] fingerprint, Guid requestId)
    {
        using var parsed = JsonDocument.Parse(envelope);
        var root = parsed.RootElement;
        if (!Guid.TryParseExact(root.GetProperty("id").GetString(), "N", out var wireId) || wireId != requestId
         || root.TryGetProperty("token", out _))
            throw Blocked("Saved VLS request identity is invalid or contains credentials.");
        var command = root.GetProperty("command");
        var expected = operation switch
        {
            VlsOperations.Setup => "setup",
            VlsOperations.SignRemote => "sign_remote",
            VlsOperations.PaymentPreimages => "payment_preimages",
            VlsOperations.ValidateHolder => "validate_holder",
            VlsOperations.Activate => "activate",
            VlsOperations.RevokeHolder => "revoke_holder",
            VlsOperations.ValidateRevocation => "validate_revocation",
            _ => throw Blocked("Saved VLS operation is outside this recovery scope.")
        };
        if (command.GetProperty("op").GetString() != expected)
            throw Blocked("Saved VLS request operation does not match its envelope.");
        var commandBytes = System.Text.Encoding.UTF8.GetBytes(command.GetRawText());
        var material = new byte[sizeof(uint) + commandBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(material, operation);
        commandBytes.CopyTo(material.AsSpan(sizeof(uint)));
        if (!CryptographicOperations.FixedTimeEquals(fingerprint, SHA256.HashData(material)))
            throw Blocked("Saved VLS request arguments do not match their fingerprint.");
        var channelBytes = Convert.FromHexString(command.GetProperty("channel").GetString()
                         ?? throw Blocked("Saved VLS request has no channel identity."));
        if (channelBytes.Length != 41)
            throw Blocked("Saved VLS channel identity is invalid.");
    }
    private void ValidateMappedChannel(byte[] envelope, SigningWorkflow workflow)
    {
        using var scope = _scopes.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var mapping = Task.Run(() => uow.VlsChannelMappingDbRepository.GetByChannelIdAsync(workflow.ChannelId))
                          .GetAwaiter().GetResult();
        using var parsed = JsonDocument.Parse(envelope);
        var vlsId = Convert.FromHexString(parsed.RootElement.GetProperty("command").GetProperty("channel").GetString()!);
        if (mapping?.VlsChannelId is null || !mapping.VlsChannelId.AsSpan().SequenceEqual(vlsId)
         || !mapping.SignerIdentity.AsSpan().SequenceEqual(workflow.SignerIdentity)
         || !string.Equals(mapping.Network, workflow.Network, StringComparison.Ordinal))
            throw Blocked("VLS request belongs to another persisted channel, signer or network.");
    }
    private static SigningWorkflow CloneWorkflow(SigningWorkflow workflow) => workflow with
    { SnapshotFingerprint = workflow.SnapshotFingerprint.ToArray(), SignerIdentity = workflow.SignerIdentity.ToArray() };
    private static StoredRequest CloneRequest(StoredRequest request) => request with
    { Envelope = request.Envelope.ToArray(), ArgumentFingerprint = request.ArgumentFingerprint.ToArray(), Response = request.Response?.ToArray() };
    private static string CanonicalNetwork(string network) => NLightning.Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(network).Name;
    private static VlsSigningWorkflowException Blocked(string message, Exception? inner = null) => new(message, inner);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_attached) _connection.DetachWorkflowCapture(this);
        // Gates may still be held by scopes unwinding a shutdown; do not dispose them underneath those owners.
    }

    private sealed class WorkflowScope(VlsSigningWorkflowCoordinator owner, SigningWorkflow workflow,
                                      List<StoredRequest> requests, SemaphoreSlim gate) : ISigningWorkflowScope
    {
        private int _ordinal;
        private int _activated;
        private int _executing;
        private bool _disposed;
        private bool _consumeStaged;
        public Guid WorkflowId => workflow.WorkflowId;
        public void Activate()
        {
            if (_disposed || Interlocked.CompareExchange(ref _activated, 1, 0) != 0)
                throw Blocked("The signing workflow scope is already active or disposed.");
            if (owner._active.Value is not null)
            { _activated = 0; throw Blocked("Signing workflows cannot be nested."); }
            owner._active.Value = this;
        }
        public byte[] Execute(uint operation, byte[] proposedEnvelope, byte[] fingerprint,
            Func<byte[], RemoteSigningRequestStatus> reconcile, Func<byte[], byte[]> execute)
        {
            if (_disposed || _consumeStaged || Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
                throw Blocked("Signing workflow request capture cannot run concurrently or after consumption.");
            try
            {
                // Commitment construction reads immutable public basepoints by key index. This lookup
                // creates no signing outcome or safety state, so it does not occupy a recovery ordinal.
                // The workflow binds the signer identity/snapshot and captures the resulting sign payload.
                if (operation is VlsOperations.Basepoints or VlsOperations.Point
                    or VlsOperations.PublicAccount or VlsOperations.WalletPublicKey)
                    return execute(proposedEnvelope.ToArray());
                if (!Allowed(workflow.Kind, operation) || fingerprint.Length != 32
                 || proposedEnvelope.Length is <= 0 or > MaximumEnvelopeBytes || _ordinal >= MaximumRequestsPerWorkflow)
                {
                    MarkWorkflowBlocked();
                    throw Blocked("The signing operation is outside this workflow's supported recovery scope.");
                }
                using var proposed = JsonDocument.Parse(proposedEnvelope);
                var proposedId = Guid.ParseExact(proposed.RootElement.GetProperty("id").GetString()!, "N");
                ValidateEnvelope(operation, proposedEnvelope, fingerprint, proposedId);
                owner.ValidateMappedChannel(proposedEnvelope, workflow);
                var prior = _ordinal < requests.Count;
                StoredRequest request;
                if (prior)
                {
                    request = requests[_ordinal];
                    if (request.Operation != operation || !request.ArgumentFingerprint.AsSpan().SequenceEqual(fingerprint))
                    {
                        BlockStored(request);
                        throw Blocked("Signing workflow request ordinal was replayed with different arguments.");
                    }
                }
                else
                {
                    var ticks = DateTime.UtcNow.Ticks;
                    request = new StoredRequest(proposedId, WorkflowId, _ordinal, operation, proposedEnvelope.ToArray(),
                        fingerprint.ToArray(), SigningRequestState.Prepared, null, ticks, ticks);
                    Persist(async uow => await uow.SigningWorkflowDbRepository.AddRequestAsync(request));
                    requests.Add(request);
                }
                if (request.State == SigningRequestState.Completed)
                {
                    var completedStatus = reconcile(request.Envelope.ToArray());
                    if (completedStatus.Outcome != RemoteSigningRequestOutcome.Completed
                     || completedStatus.Response is null
                     || !completedStatus.Response.AsSpan().SequenceEqual(request.Response))
                    {
                        BlockStored(request);
                        throw Blocked("The signer's durable receipt no longer matches the node's completed signing request.");
                    }
                    _ordinal++;
                    return request.Response!.ToArray();
                }
                var status = reconcile(request.Envelope.ToArray());
                byte[] response;
                if (status.Outcome == RemoteSigningRequestOutcome.Completed && status.Response is not null)
                    response = status.Response.ToArray();
                else if (status.Outcome == RemoteSigningRequestOutcome.NotFound)
                    response = execute(request.Envelope.ToArray());
                else
                {
                    BlockStored(request);
                    throw Blocked("Signer request cannot safely resume: " + status.Outcome);
                }
                if (response.Length > 4_194_304)
                    throw Blocked("Signer workflow response exceeds the supported payload limit.");
                var completed = request with
                {
                    State = SigningRequestState.Completed,
                    Response = response.ToArray(),
                    UpdatedAtTicks = DateTime.UtcNow.Ticks
                };
                Persist(async uow => await uow.SigningWorkflowDbRepository.UpdateRequestAsync(completed));
                requests[_ordinal] = completed;
                _ordinal++;
                return response.ToArray();
            }
            catch (SignerException ex)
            {
                if (_ordinal < requests.Count) BlockStored(requests[_ordinal]);
                throw Blocked("Signer refused the workflow request; recovery requires attention.", ex);
            }
            finally { Volatile.Write(ref _executing, 0); }
        }
        public async Task StageConsumeAsync(IUnitOfWork unitOfWork)
        {
            ArgumentNullException.ThrowIfNull(unitOfWork);
            if (_disposed || _activated == 0 || !ReferenceEquals(owner._active.Value, this)
             || _consumeStaged || Volatile.Read(ref _executing) != 0
             || _ordinal != requests.Count || requests.Count == 0
             || requests.Any(request => request.State != SigningRequestState.Completed))
                throw Blocked("A signing workflow may be consumed only after its complete saved request sequence is replayed.");
            _consumeStaged = true;
            await unitOfWork.SigningWorkflowDbRepository.ConsumeWorkflowAsync(WorkflowId);
        }
        private void BlockStored(StoredRequest request)
        {
            var ticks = DateTime.UtcNow.Ticks;
            var blocked = request with { State = SigningRequestState.Blocked, UpdatedAtTicks = ticks };
            Persist(async uow =>
            {
                await uow.SigningWorkflowDbRepository.UpdateRequestAsync(blocked);
                await uow.SigningWorkflowDbRepository.UpdateWorkflowAsync(workflow with
                { State = SigningWorkflowState.Blocked, UpdatedAtTicks = ticks });
            });
            requests[request.Ordinal] = blocked;
        }
        private void MarkWorkflowBlocked()
        {
            Persist(async uow => await uow.SigningWorkflowDbRepository.UpdateWorkflowAsync(workflow with
            { State = SigningWorkflowState.Blocked, UpdatedAtTicks = DateTime.UtcNow.Ticks }));
        }
        private void Persist(Func<IUnitOfWork, Task> stage)
        {
            Task.Run(async () =>
            {
                using var scope = owner._scopes.CreateScope();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await stage(uow);
                await uow.SaveChangesAsync();
            }).GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            if (_disposed) return;
            if (Volatile.Read(ref _executing) != 0)
                throw Blocked("The signing workflow cannot be disposed while a request is executing.");
            _disposed = true;
            if (ReferenceEquals(owner._active.Value, this)) owner._active.Value = null;
            gate.Release();
        }
    }
}

/// <summary>Workflow uncertainty is a local recovery condition, never evidence of a peer's bad signature.</summary>
public sealed class VlsSigningWorkflowException(string message, Exception? inner = null) : Exception(message, inner);