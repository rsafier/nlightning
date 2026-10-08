using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Domain.Signing.Recovery;
using StoredRequest = NLightning.Domain.Signing.Recovery.SigningRequest;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Captures requests only inside validated application transitions; recovery never guesses a new request ID.</summary>
public sealed class RemoteSigningWorkflowCoordinator : IRemoteSigningWorkflowCoordinator, IRemoteSigningRequestCapture, INativeFundingSigningRecovery, INativeWalletSigningRecovery, INativeSweepSigningRecovery, IDisposable
{
    public const int SchemaVersion = 1;
    public const int MaximumEnvelopeBytes = 1024 * 1024;
    public const int MaximumRequestsPerWorkflow = 256;
    private readonly RemoteSignerConnection _connection;
    private readonly IServiceScopeFactory _scopes;
    private readonly AsyncLocal<WorkflowScope?> _active = new();
    private readonly bool _attached;
    private bool _disposed;
    private readonly ConcurrentDictionary<ChannelId, SemaphoreSlim> _channelGates = new();

    public RemoteSigningWorkflowCoordinator(RemoteSignerConnection connection, IServiceScopeFactory scopes, bool attachCapture = true)
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
        if (descriptor.Kind != SigningWorkflowKind.ReleaseRevoke)
            throw Blocked("Only a post-save revocation release can be staged with a channel transition.");
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

    public Domain.Bitcoin.ValueObjects.SignedTransaction ReplayFunding(ISigningWorkflowScope workflow)
    {
        if (workflow is not WorkflowScope active || !ReferenceEquals(_active.Value, active))
            throw Blocked("Funding recovery requires its activated workflow scope.");
        var requests = active.SavedRequests;
        if (active.Kind != SigningWorkflowKind.Funding
         || requests.Count(request => request.Operation == SignerOperations.SignFundingTransaction) != 1
         || requests[^1].Operation != SignerOperations.SignFundingTransaction)
            throw Blocked("Funding recovery has no complete saved funding signing intent.");
        byte[]? fundingResponse = null;
        foreach (var request in requests)
        {
            var response = Execute(request.Operation, request.Envelope, request.ArgumentFingerprint,
                envelope => RemoteSignerConnection.ToWorkflowStatus(_connection.Reconcile(WireRequest.Parser.ParseFrom(envelope))),
                envelope => _connection.ExecutePayload(WireRequest.Parser.ParseFrom(envelope)));
            if (request.Operation == SignerOperations.SignFundingTransaction) fundingResponse = response;
        }
        var result = SignerWire.Decode(fundingResponse!);
        if (result.Length != 2 || !SignerWire.Read<bool>(result[0]))
            throw Blocked("The recovered funding receipt did not sign every input.");
        return SignerWire.Read<Domain.Bitcoin.ValueObjects.SignedTransaction>(result[1]);
    }

    public Domain.Bitcoin.ValueObjects.SignedTransaction? ReplayWithdrawal(ISigningWorkflowScope workflow)
    {
        if (workflow is not WorkflowScope active || !ReferenceEquals(_active.Value, active)
         || active.Kind != SigningWorkflowKind.WalletWithdrawal)
            throw Blocked("Withdrawal recovery requires its activated workflow scope.");
        var requests = active.SavedRequests;
        if (requests.Count == 0) return null; // Intent saved before any request dispatch.
        if (requests.Count != 1 || requests[0].Operation != SignerOperations.SignWalletTransaction3)
            throw Blocked("Withdrawal recovery contains an unexpected request sequence.");
        var request = requests[0];
        var response = Execute(request.Operation, request.Envelope, request.ArgumentFingerprint,
            envelope => RemoteSignerConnection.ToWorkflowStatus(_connection.Reconcile(WireRequest.Parser.ParseFrom(envelope))),
            envelope => _connection.ExecutePayload(WireRequest.Parser.ParseFrom(envelope)));
        var result = SignerWire.Decode(response);
        if (result.Length != 2 || !SignerWire.Read<bool>(result[0]))
            throw Blocked("The recovered withdrawal receipt did not sign every input.");
        return SignerWire.Read<Domain.Bitcoin.ValueObjects.SignedTransaction>(result[1]);
    }

    public Task StageRetireSweepAsync(SigningWorkflowDescriptor descriptor, IUnitOfWork unitOfWork)
        => StageRetireSweepAsync(descriptor, unitOfWork, envelope => RemoteSignerConnection.ToWorkflowStatus(
            _connection.Reconcile(WireRequest.Parser.ParseFrom(envelope))));

    internal async Task StageRetireSweepAsync(SigningWorkflowDescriptor descriptor, IUnitOfWork unitOfWork,
        Func<byte[], RemoteSigningRequestStatus> reconcile)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (descriptor.Kind != SigningWorkflowKind.OnchainSweep)
            throw Blocked("Only an obsolete sweep intent may be retired without publication.");
        using var scope = await BeginAsync(descriptor);
        scope.Activate();
        await ((WorkflowScope)scope).StageRetireAsync(unitOfWork, reconcile);
    }

    public byte[] EncodeSweepContexts(IReadOnlyList<Domain.Onchain.Models.SweepSigningContext> contexts)
        => JsonSerializer.SerializeToUtf8Bytes(contexts, SignerWire.Options);

    public IReadOnlyList<Domain.Crypto.ValueObjects.CompactSignature> SignSweepInputs(ISigningWorkflowScope workflow, Domain.Bitcoin.Interfaces.ILightningSigner signer)
    {
        if (workflow is not WorkflowScope active || !ReferenceEquals(_active.Value, active)
         || active.Kind != SigningWorkflowKind.OnchainSweep)
            throw Blocked("Sweep recovery requires its activated workflow scope.");
        using var intent = JsonDocument.Parse(active.PublicationIntent
            ?? throw Blocked("Sweep recovery lost its publication intent."));
        var contexts = JsonSerializer.Deserialize<Domain.Onchain.Models.SweepSigningContext[]>(
            intent.RootElement.GetProperty("Contexts").GetBytesFromBase64(), SignerWire.Options)
            ?? throw Blocked("Sweep recovery lost its input contexts.");
        if (contexts.Length is <= 0 or > MaximumRequestsPerWorkflow)
            throw Blocked("Sweep recovery has an invalid input count.");
        var signatures = new List<Domain.Crypto.ValueObjects.CompactSignature>();
        var requests = active.SavedRequests;
        foreach (var request in requests)
        {
            if (request.Operation == SignerOperations.SignSweepInput)
            {
                var args = SignerWire.Decode(WireRequest.Parser.ParseFrom(request.Envelope).Payload.ToByteArray());
                if (SignerWire.Read<Domain.Onchain.Models.SweepSigningContext>(args[1]).InputIndex != signatures.Count)
                    throw Blocked("Saved sweep requests do not follow input order.");
            }
            var response = Execute(request.Operation, request.Envelope, request.ArgumentFingerprint,
                envelope => RemoteSignerConnection.ToWorkflowStatus(_connection.Reconcile(WireRequest.Parser.ParseFrom(envelope))),
                envelope => _connection.ExecutePayload(WireRequest.Parser.ParseFrom(envelope)));
            if (request.Operation == SignerOperations.SignSweepInput)
                signatures.Add(SignerWire.Read<Domain.Crypto.ValueObjects.CompactSignature>(SignerWire.Decode(response)[0]));
        }
        for (var i = signatures.Count; i < contexts.Length; i++)
        {
            if (contexts[i].InputIndex != i)
                throw Blocked("Saved sweep contexts do not follow input order.");
            if (requests.Count == 0 && i == 0)
                signatures.Add(signer.SignSweepInput(active.ChannelId, contexts[i]));
            else
                signatures.Add(SignerWire.Read<Domain.Crypto.ValueObjects.CompactSignature>(
                    _connection.Invoke(SignerOperations.SignSweepInput, active.ChannelId, contexts[i])[0]));
        }
        return signatures;
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
            CanonicalNetwork(_connection.Identity.Network), SchemaVersion, SigningWorkflowState.Pending, ticks, ticks)
        { PublicationIntent = descriptor.PublicationIntent?.ToArray() };
    }

    private void ValidateWorkflow(SigningWorkflow workflow, SigningWorkflowDescriptor descriptor)
    {
        if (workflow.State != SigningWorkflowState.Pending || workflow.SchemaVersion != SchemaVersion
         || !workflow.SignerIdentity.AsSpan().SequenceEqual((byte[])_connection.Identity.NodePublicKey)
         || !string.Equals(workflow.Network, CanonicalNetwork(_connection.Identity.Network), StringComparison.Ordinal)
         || workflow.ChannelId != descriptor.ChannelId || workflow.Kind != descriptor.Kind
         || workflow.ExpectedLocalCommitmentNumber != descriptor.ExpectedLocalCommitmentNumber
         || workflow.ExpectedRemoteCommitmentNumber != descriptor.ExpectedRemoteCommitmentNumber
         || !(workflow.PublicationIntent ?? []).AsSpan().SequenceEqual(descriptor.PublicationIntent ?? [])
         || !workflow.SnapshotFingerprint.AsSpan().SequenceEqual(descriptor.SnapshotFingerprint))
            throw Blocked("Pending signing workflow does not match the current channel snapshot, signer or network.");
    }
    private static void ValidateDescriptor(SigningWorkflowDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.SnapshotFingerprint is not { Length: 32 }
         || descriptor.PublicationIntent?.Length > MaximumEnvelopeBytes
         || descriptor.Kind is (SigningWorkflowKind.WalletWithdrawal or SigningWorkflowKind.OnchainSweep) && (descriptor.PublicationIntent is null
             || !SHA256.HashData(descriptor.PublicationIntent).AsSpan().SequenceEqual(descriptor.SnapshotFingerprint))
         || descriptor.Kind is not (SigningWorkflowKind.SendCommit or SigningWorkflowKind.ReleaseRevoke or SigningWorkflowKind.Funding or SigningWorkflowKind.WalletWithdrawal or SigningWorkflowKind.OnchainSweep))
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
            ValidateEnvelope(request.Operation, request.Envelope, request.ArgumentFingerprint, request.RequestId,
                workflow.ChannelId, workflow.Kind, workflow.PublicationIntent);
        }
    }
    private static bool Allowed(SigningWorkflowKind kind, uint operation) => kind != SigningWorkflowKind.WalletWithdrawal && operation == SignerOperations.RegisterChannel
        || kind == SigningWorkflowKind.SendCommit && operation is SignerOperations.SignChannelTransaction
            or SignerOperations.SignChannelTransaction2 or SignerOperations.SignRemoteHtlcTransactions
            or SignerOperations.SignRemoteCommitmentPartial
        || kind == SigningWorkflowKind.WalletWithdrawal && operation == SignerOperations.SignWalletTransaction3
        || kind == SigningWorkflowKind.OnchainSweep && operation == SignerOperations.SignSweepInput
        || kind == SigningWorkflowKind.Funding && operation == SignerOperations.SignFundingTransaction
        || kind == SigningWorkflowKind.ReleaseRevoke && operation is SignerOperations.AdvanceLocalCommitment
            or SignerOperations.RevealPerCommitmentSecret or SignerOperations.GetPerCommitmentPoint2
            or SignerOperations.GetLocalVerificationNonce2 or SignerOperations.GetLocalVerificationNonce3;
    private static void ValidateEnvelope(uint operation, byte[] envelope, byte[] fingerprint, Guid requestId,
                                        ChannelId channel, SigningWorkflowKind kind, byte[]? publicationIntent)
    {
        var parsed = WireRequest.Parser.ParseFrom(envelope);
        if (parsed.Version != 1 || parsed.Operation != operation
         || !Guid.TryParseExact(parsed.RequestId, "N", out var wireId) || wireId != requestId
         || !DurableSignerState.SupportsReconciliation(operation))
            throw Blocked("Saved signing request identity or operation is invalid.");
        var material = new byte[sizeof(uint) + parsed.Payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(material, operation);
        parsed.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));
        if (!CryptographicOperations.FixedTimeEquals(fingerprint, SHA256.HashData(material)))
            throw Blocked("Saved signing request arguments do not match their fingerprint.");
        var args = SignerWire.Decode(parsed.Payload.ToByteArray());
        if (args.Length != SignerOperations.ArgumentCount(operation) || kind != SigningWorkflowKind.WalletWithdrawal && SignerWire.Read<ChannelId>(args[0]) != channel)
            throw Blocked("Saved signing request belongs to another channel or has invalid arguments.");
        if (kind == SigningWorkflowKind.OnchainSweep && operation == SignerOperations.SignSweepInput)
        {
            using var intent = JsonDocument.Parse(publicationIntent
                ?? throw Blocked("Sweep signing request lost its publication intent."));
            var contexts = JsonSerializer.Deserialize<Domain.Onchain.Models.SweepSigningContext[]>(
                intent.RootElement.GetProperty("Contexts").GetBytesFromBase64(), SignerWire.Options)
                ?? throw Blocked("Sweep signing request lost its input contexts.");
            var context = SignerWire.Read<Domain.Onchain.Models.SweepSigningContext>(args[1]);
            if (context.InputIndex < 0 || context.InputIndex >= contexts.Length
             || !SignerWire.Encode([context]).AsSpan().SequenceEqual(SignerWire.Encode([contexts[context.InputIndex]])))
                throw Blocked("Sweep signing request differs from the saved publication intent.");
        }
        if (kind == SigningWorkflowKind.WalletWithdrawal)
        {
            using var intent = JsonDocument.Parse(publicationIntent
                ?? throw Blocked("Withdrawal signing request lost its publication intent."));
            var unsigned = SignerWire.Read<Domain.Bitcoin.ValueObjects.SignedTransaction>(args[0]);
            if (SignerWire.Read<Guid>(args[1]) != intent.RootElement.GetProperty("ReservationId").GetGuid()
             || !unsigned.RawTxBytes.AsSpan().SequenceEqual(intent.RootElement.GetProperty("UnsignedTransaction").GetBytesFromBase64())
             || SignerWire.Read<Domain.Bitcoin.Wallet.Models.SpentOutput[]>(args[2]).Length != 0)
                throw Blocked("Withdrawal signing request differs from the saved publication intent.");
        }
    }
    private static SigningWorkflow CloneWorkflow(SigningWorkflow workflow) => workflow with
    { SnapshotFingerprint = workflow.SnapshotFingerprint.ToArray(), SignerIdentity = workflow.SignerIdentity.ToArray(), PublicationIntent = workflow.PublicationIntent?.ToArray() };
    private static StoredRequest CloneRequest(StoredRequest request) => request with
    { Envelope = request.Envelope.ToArray(), ArgumentFingerprint = request.ArgumentFingerprint.ToArray(), Response = request.Response?.ToArray() };
    private static string CanonicalNetwork(string network) => NLightning.Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(network).Name;
    private static RemoteSigningWorkflowException Blocked(string message, Exception? inner = null) => new(message, inner);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_attached) _connection.DetachWorkflowCapture(this);
        // Gates may still be held by scopes unwinding a shutdown; do not dispose them underneath those owners.
    }

    private sealed class WorkflowScope(RemoteSigningWorkflowCoordinator owner, SigningWorkflow workflow,
                                      List<StoredRequest> requests, SemaphoreSlim gate) : ISigningWorkflowScope
    {
        private int _ordinal;
        private int _activated;
        private int _executing;
        private bool _disposed;
        private bool _consumeStaged;
        public Guid WorkflowId => workflow.WorkflowId;
        public SigningWorkflowKind Kind => workflow.Kind;
        public ChannelId ChannelId => workflow.ChannelId;
        public byte[]? PublicationIntent => workflow.PublicationIntent;
        public IReadOnlyList<StoredRequest> SavedRequests => requests.ToArray();
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
                if (operation == SignerOperations.GetChannelBasepoints)
                    return execute(proposedEnvelope.ToArray());
                if (!Allowed(workflow.Kind, operation) || fingerprint.Length != 32
                 || proposedEnvelope.Length is <= 0 or > MaximumEnvelopeBytes || _ordinal >= MaximumRequestsPerWorkflow)
                {
                    MarkWorkflowBlocked();
                    throw Blocked("The signing operation is outside this workflow's supported recovery scope.");
                }
                var proposed = WireRequest.Parser.ParseFrom(proposedEnvelope);
                var proposedId = Guid.ParseExact(proposed.RequestId, "N");
                ValidateEnvelope(operation, proposedEnvelope, fingerprint, proposedId, workflow.ChannelId, workflow.Kind, workflow.PublicationIntent);
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
                if (response.Length > RemoteSignerOptions.MaxMessageBytes)
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
        public async Task StageRetireAsync(IUnitOfWork unitOfWork, Func<byte[], RemoteSigningRequestStatus> reconcile)
        {
            if (_disposed || _activated == 0 || !ReferenceEquals(owner._active.Value, this)
             || _consumeStaged || Volatile.Read(ref _executing) != 0 || workflow.Kind != SigningWorkflowKind.OnchainSweep)
                throw Blocked("Sweep retirement requires its activated pending workflow scope.");
            foreach (var request in requests)
            {
                RemoteSigningRequestStatus status;
                try
                {
                    status = reconcile(request.Envelope.ToArray());
                }
                catch (SignerException exception)
                {
                    BlockStored(request);
                    throw Blocked("Signer refused reconciliation of an obsolete sweep request.", exception);
                }
                if (status.Outcome == RemoteSigningRequestOutcome.Completed && status.Response is not null
                 && status.Response.Length <= RemoteSignerOptions.MaxMessageBytes
                 && (request.State == SigningRequestState.Prepared
                  || status.Response.AsSpan().SequenceEqual(request.Response)))
                {
                    if (request.State == SigningRequestState.Prepared)
                        await unitOfWork.SigningWorkflowDbRepository.UpdateRequestAsync(request with
                        {
                            State = SigningRequestState.Completed,
                            Response = status.Response.ToArray(),
                            UpdatedAtTicks = DateTime.UtcNow.Ticks
                        });
                }
                else if (status.Outcome != RemoteSigningRequestOutcome.NotFound
                      || request.State != SigningRequestState.Prepared)
                {
                    BlockStored(request);
                    throw Blocked("Obsolete sweep request cannot safely retire: " + status.Outcome);
                }
            }
            _consumeStaged = true;
            await unitOfWork.SigningWorkflowDbRepository.UpdateWorkflowAsync(workflow with
            { State = SigningWorkflowState.Abandoned, UpdatedAtTicks = DateTime.UtcNow.Ticks });
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
public sealed class RemoteSigningWorkflowException(string message, Exception? inner = null) : Exception(message, inner);