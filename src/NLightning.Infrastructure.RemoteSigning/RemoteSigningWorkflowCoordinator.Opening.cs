using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Domain.Signing.Recovery;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed partial class RemoteSigningWorkflowCoordinator : INativeOpeningSigningRecovery
{
    public Task<NativeOpeningReply> ReadOpeningReplyAsync(SigningWorkflow workflow)
        => ReadOpeningReplyAsync(workflow, envelope => RemoteSignerConnection.ToWorkflowStatus(
            _connection.Reconcile(WireRequest.Parser.ParseFrom(envelope))));

    internal async Task<NativeOpeningReply> ReadOpeningReplyAsync(SigningWorkflow workflow,
        Func<byte[], RemoteSigningRequestStatus> reconcile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (workflow.Kind != SigningWorkflowKind.Opening || workflow.State != SigningWorkflowState.Consumed
         || workflow.SchemaVersion != SchemaVersion || workflow.PublicationIntent is null
         || workflow.SnapshotFingerprint is not { Length: 32 }
         || !SHA256.HashData(workflow.PublicationIntent).AsSpan().SequenceEqual(workflow.SnapshotFingerprint)
         || !workflow.SignerIdentity.AsSpan().SequenceEqual((byte[])_connection.Identity.NodePublicKey)
         || workflow.Network != CanonicalNetwork(_connection.Identity.Network))
            throw Blocked("Initial opening reply requires its consumed immutable signer workflow.");
        using var scope = _scopes.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var stored = await uow.SigningWorkflowDbRepository.GetAsync(workflow.WorkflowId);
        if (stored is null || stored.State != SigningWorkflowState.Consumed
         || stored.ChannelId != workflow.ChannelId || stored.Kind != workflow.Kind
         || stored.SchemaVersion != workflow.SchemaVersion || stored.Network != workflow.Network
         || stored.ExpectedLocalCommitmentNumber != workflow.ExpectedLocalCommitmentNumber
         || stored.ExpectedRemoteCommitmentNumber != workflow.ExpectedRemoteCommitmentNumber
         || !stored.SignerIdentity.AsSpan().SequenceEqual(workflow.SignerIdentity)
         || !stored.SnapshotFingerprint.AsSpan().SequenceEqual(workflow.SnapshotFingerprint)
         || !(stored.PublicationIntent ?? []).AsSpan().SequenceEqual(workflow.PublicationIntent))
            throw Blocked("Initial opening reply differs from its durable consumed workflow.");
        var requests = await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId);
        if (requests.Count < 2 || requests.Any(request => request.State != SigningRequestState.Consumed || request.Response is null)
         || requests.Take(requests.Count - 1).Any(request => request.Operation != SignerOperations.RegisterChannel)
         || requests[^1].Operation is not (SignerOperations.SignChannelTransaction or SignerOperations.SignRemoteCommitmentPartial))
            throw Blocked("Initial opening reply has no complete consumed request sequence.");
        ValidateRequests(stored, requests.Select(request => request with { State = SigningRequestState.Completed }).ToArray());
        var registrationPayload = WireRequest.Parser.ParseFrom(requests[0].Envelope).Payload;
        if (requests.Take(requests.Count - 1).Any(request =>
            !WireRequest.Parser.ParseFrom(request.Envelope).Payload.Span.SequenceEqual(registrationPayload.Span)))
            throw Blocked("Initial opening reply contains different channel registrations.");
        foreach (var request in requests)
        {
            var status = reconcile(request.Envelope.ToArray());
            if (status.Outcome != RemoteSigningRequestOutcome.Completed || status.Response is null
             || !status.Response.AsSpan().SequenceEqual(request.Response))
                throw Blocked("Initial opening reply no longer matches its exact durable signer receipt.");
        }
        var result = SignerWire.Decode(requests[^1].Response!);
        if (result.Length != 1) throw Blocked("Initial opening reply has an invalid signature result.");
        return requests[^1].Operation == SignerOperations.SignChannelTransaction
            ? new NativeOpeningReply(SignerWire.Read<CompactSignature>(result[0]), null)
            : new NativeOpeningReply(null, SignerWire.Read<MusigPartialSignatureWithNonce>(result[0]));
    }
}