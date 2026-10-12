using System.Text.Json;
using NBitcoin;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Signing.Recovery;
using NLightning.Domain.Signing.Vls;
using CompactSignature = NLightning.Domain.Crypto.ValueObjects.CompactSignature;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>
/// Funding and close signing as node workflows: the intent row is written in the same save as the first request
/// envelope (its original request ID), the receipt is saved before use, the application consumes the workflow in the
/// save of its transition, and recovery replays only the saved envelope.
/// </summary>
public sealed partial class VlsSigningWorkflowCoordinator : INativeFundingSigningRecovery, IVlsCloseSigningRecovery
{
    /// <summary>Intents whose prerequisites are only the request itself: no row is saved until its envelope is.</summary>
    private static bool SavesIntentWithFirstRequest(SigningWorkflowKind kind) =>
        kind is SigningWorkflowKind.Funding or SigningWorkflowKind.MutualClose or SigningWorkflowKind.ForceClose;

    public SignedTransaction ReplayFunding(ISigningWorkflowScope workflow)
    {
        var request = SingleSavedRequest(workflow, SigningWorkflowKind.Funding, VlsOperations.WalletSign);
        var response = Execute(request.Operation, request.Envelope, request.ArgumentFingerprint,
                               _connection.ReconcileEnvelope, _connection.ExecuteEnvelope);
        try
        {
            using var envelope = JsonDocument.Parse(request.Envelope);
            using var result = JsonDocument.Parse(response);
            var tx = Transaction.Parse(envelope.RootElement.GetProperty("command").GetProperty("transaction").GetString()!,
                                      Network.RegTest);
            var witnesses = result.RootElement.GetProperty("witnesses").EnumerateArray().ToArray();
            if (witnesses.Length != tx.Inputs.Count)
                throw Blocked("The recovered funding receipt does not witness every input.");
            for (var i = 0; i < witnesses.Length; i++)
                tx.Inputs[i].WitScript = new WitScript(witnesses[i].EnumerateArray()
                                                                   .Select(w => Convert.FromHexString(w.GetString()!))
                                                                   .ToArray());
            return new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException
                                      or FormatException or ArgumentException)
        {
            throw Blocked("The recovered funding receipt cannot be read.", e);
        }
    }

    public async Task<ISigningWorkflowScope> ResumeSavedMutualCloseAsync(SigningWorkflow saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Kind != SigningWorkflowKind.MutualClose || saved.State != SigningWorkflowState.Pending)
            throw Blocked("Only a pending mutual close intent can be resumed from its saved request.");
        var scope = await BeginAsync(new SigningWorkflowDescriptor(saved.ChannelId, saved.Kind,
            saved.ExpectedLocalCommitmentNumber, saved.ExpectedRemoteCommitmentNumber, saved.SnapshotFingerprint));
        try
        {
            var active = (WorkflowScope)scope;
            if (active.SavedRequests is not [{ Operation: VlsOperations.MutualClose } request])
                throw Blocked("The saved mutual close intent has no single saved request.");
            using var envelope = JsonDocument.Parse(request.Envelope);
            var command = envelope.RootElement.GetProperty("command");
            var fingerprint = VlsCloseFingerprint.MutualClose(command.GetProperty("holder_sat").GetUInt64(),
                command.GetProperty("peer_sat").GetUInt64(), Script(command, "holder_script"),
                Script(command, "peer_script"));
            if (!fingerprint.AsSpan().SequenceEqual(saved.SnapshotFingerprint))
            {
                active.BlockSaved();
                throw Blocked("The saved mutual close request does not match its intent.");
            }
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public CompactSignature ReplayCloseSignature(ISigningWorkflowScope workflow)
    {
        var kind = (workflow as WorkflowScope)?.Kind;
        var request = kind == SigningWorkflowKind.ForceClose
                          ? SingleSavedRequest(workflow, SigningWorkflowKind.ForceClose, VlsOperations.ForceClose)
                          : SingleSavedRequest(workflow, SigningWorkflowKind.MutualClose, VlsOperations.MutualClose);
        var response = Execute(request.Operation, request.Envelope, request.ArgumentFingerprint,
                               _connection.ReconcileEnvelope, _connection.ExecuteEnvelope);
        using var result = JsonDocument.Parse(response);
        return new CompactSignature(Convert.FromHexString(result.RootElement.GetProperty("signature").GetString()!));
    }

    private SigningRequest SingleSavedRequest(ISigningWorkflowScope workflow, SigningWorkflowKind kind,
                                              uint operation)
    {
        if (workflow is not WorkflowScope active || !ReferenceEquals(_active.Value, active) || active.Kind != kind)
            throw Blocked("Recovery requires its activated workflow scope.");
        if (active.SavedRequests is not [var request] || request.Operation != operation)
            throw Blocked("Recovery has no single saved signing request to replay.");
        return request;
    }

    private static byte[]? Script(JsonElement command, string name) =>
        command.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Convert.FromHexString(value.GetString()!)
            : null;
}