using System.Collections.Immutable;

namespace NLightning.Application.Tests.InteractiveTx.TestDoubles;

using Application.InteractiveTx.Interfaces;
using Application.InteractiveTx.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// A small reference engine for the driver tests and the harness while lane IT-A's
/// <see cref="InteractiveTxSession"/> is not merged: turns (IT-S-02), serial id parity and uniqueness (IT-S-01), the
/// basic IT-R-01..03 checks, the input/output balance of IT-R-04, the tx_signatures order (IT-SIG-01), the tx_abort echo
/// (IT-ABT-01) and the RBF double-spend (IT-RBF-01). It is a test double, not a spec implementation: the harness runs
/// the real engine too once it exists (<see cref="InteractiveTxEngines"/>).
/// </summary>
internal sealed class ReferenceInteractiveTxEngine : IInteractiveTxEngine
{
    public IInteractiveTxNegotiation Create(InteractiveTxSessionParameters parameters) =>
        ReferenceNegotiation.Create(parameters);

    public IInteractiveTxNegotiation Restore(InteractiveTxSessionModel model,
                                             InteractiveTxSessionParameters parameters) =>
        ReferenceNegotiation.Restore(model, parameters);
}

internal sealed record ReferenceNegotiation : IInteractiveTxNegotiation
{
    private static readonly LightningMoney s_dustLimit = LightningMoney.Satoshis(330);
    private static readonly LightningMoney s_maxMoney = LightningMoney.Satoshis(2_100_000_000_000_000L);

    public required InteractiveTxSessionParameters Parameters { get; init; }
    public InteractiveTxSessionState State { get; init; }
    public ImmutableList<InteractiveTxInput> InputList { get; init; } = [];
    public ImmutableList<InteractiveTxOutput> OutputList { get; init; } = [];
    public IReadOnlyList<InteractiveTxInput> Inputs => InputList;
    public IReadOnlyList<InteractiveTxOutput> Outputs => OutputList;
    public ConstructedInteractiveTx? ConstructedTx { get; init; }
    public IReadOnlyList<Witness>? RemoteWitnesses { get; init; }
    public CompactSignature? RemoteSharedInputSignature { get; init; }

    private ImmutableQueue<LocalAction> Pending { get; init; } = [];
    private bool IsOurTurn { get; init; }
    private bool Started { get; init; }
    private bool LastSentComplete { get; init; }
    private bool NegotiationDone { get; init; }
    private bool AbortSent { get; init; }
    private bool CommitmentSignedReceived { get; init; }
    private ulong NextSerialId { get; init; }

    // Set when the message we answer is a tx_complete: our tx_complete then ends the negotiation
    private bool ReceivedComplete { get; init; }

    public static ReferenceNegotiation Create(InteractiveTxSessionParameters parameters)
    {
        var actions = new List<LocalAction>();
        if (parameters is { IsInitiator: true, SharedFunding.SharedInput: { } sharedInput })
            actions.Add(new LocalAction(null, null, sharedInput, false));
        actions.AddRange(parameters.LocalContribution.Inputs.Select(i => new LocalAction(i, null, null, false)));
        if (parameters is { IsInitiator: true, SharedFunding: not null })
            actions.Add(new LocalAction(null, null, null, true));
        actions.AddRange(parameters.LocalContribution.Outputs.Select(o => new LocalAction(null, o, null, false)));

        return new ReferenceNegotiation
        {
            Parameters = parameters,
            State = InteractiveTxSessionState.Negotiating,
            Pending = ImmutableQueue.CreateRange(actions),
            IsOurTurn = parameters.IsInitiator,
            NextSerialId = parameters.IsInitiator ? 0UL : 1UL
        };
    }

    public static ReferenceNegotiation Restore(InteractiveTxSessionModel model,
                                               InteractiveTxSessionParameters parameters) =>
        new()
        {
            Parameters = parameters,
            State = model.State,
            InputList = [.. model.Inputs],
            OutputList = [.. model.Outputs],
            ConstructedTx = model.ConstructedTx,
            RemoteWitnesses = model.TheirWitnesses,
            RemoteSharedInputSignature = model.TheirSharedInputSignature,
            Started = true,
            NegotiationDone = true,
            CommitmentSignedReceived = model.CommitmentSignedReceived
        };

    public InteractiveTxNegotiationStep Start()
    {
        if (!Parameters.IsInitiator || Started)
            throw new InvalidOperationException("Only the initiator starts, once");

        return (this with { Started = true }).SendNext();
    }

    public InteractiveTxNegotiationStep Receive(IChannelMessage message, IPrevTxInspector prevTxInspector)
    {
        if (message is TxAbortMessage)
        {
            var aborted = this with { State = InteractiveTxSessionState.Aborted };
            return AbortSent
                       ? new InteractiveTxNegotiationStep(aborted, [], false, "echo", "IT-ABT-01")
                       : new InteractiveTxNegotiationStep(
                           aborted, [Abort(Parameters.ChannelId, "echo")], false, "peer aborted", "IT-ABT-01");
        }

        if (message is TxSignaturesMessage signatures)
            return ReceiveSignatures(signatures);

        if (State != InteractiveTxSessionState.Negotiating || NegotiationDone)
            return Fail($"{message.Type} after the negotiation", "IT-S-02");
        if (IsOurTurn)
            return Fail($"{message.Type} out of turn", "IT-S-02");

        var next = this with { Started = true, IsOurTurn = true, ReceivedComplete = false };
        switch (message)
        {
            case TxAddInputMessage addInput:
                var input = next.CheckInput(addInput, prevTxInspector, out var inputError);
                if (input is null)
                    return Fail(inputError!, "IT-R-01");
                next = next with { InputList = next.InputList.Add(input) };
                break;

            case TxAddOutputMessage addOutput:
                var output = next.CheckOutput(addOutput.Payload, out var outputError);
                if (output is null)
                    return Fail(outputError!, "IT-R-02");
                next = next with { OutputList = next.OutputList.Add(output) };
                break;

            case TxRemoveInputMessage removeInput:
                var removedInput = next.InputList.FirstOrDefault(i => i.SerialId == removeInput.Payload.SerialId
                                                                   && i.AddedBy == InteractiveTxParty.Remote);
                if (removedInput is null)
                    return Fail("tx_remove_input of an input the peer did not add", "IT-R-03");
                next = next with { InputList = next.InputList.Remove(removedInput) };
                break;

            case TxRemoveOutputMessage removeOutput:
                var removedOutput = next.OutputList.FirstOrDefault(o => o.SerialId == removeOutput.Payload.SerialId
                                                                     && o.AddedBy == InteractiveTxParty.Remote);
                if (removedOutput is null)
                    return Fail("tx_remove_output of an output the peer did not add", "IT-R-03");
                next = next with { OutputList = next.OutputList.Remove(removedOutput) };
                break;

            case TxCompleteMessage:
                if (LastSentComplete)
                    return next.CompleteNegotiation([]);
                next = next with { ReceivedComplete = true };
                break;

            default:
                throw new ArgumentException($"{message.Type} is not an interactive-tx message", nameof(message));
        }

        return next.SendNext();
    }

    public IInteractiveTxNegotiation WithConstructedTransaction(ConstructedInteractiveTx transaction)
    {
        if (!NegotiationDone || State != InteractiveTxSessionState.Negotiating)
            throw new InvalidOperationException("The negotiation is not complete");

        return this with { ConstructedTx = transaction, State = InteractiveTxSessionState.AwaitingCommitmentSigned };
    }

    public IInteractiveTxNegotiation OnCommitmentSignedReceived()
    {
        if (State != InteractiveTxSessionState.AwaitingCommitmentSigned)
            throw new InvalidOperationException($"commitment_signed in state {State}");

        return this with
        {
            State = InteractiveTxSessionState.AwaitingTxSignatures,
            CommitmentSignedReceived = true
        };
    }

    public bool SendsTxSignaturesFirst()
    {
        var local = SumContributed(InteractiveTxParty.Local, Parameters.IsInitiator);
        var remote = SumContributed(InteractiveTxParty.Remote, !Parameters.IsInitiator);
        if (local != remote)
            return local < remote;

        return ((byte[])Parameters.LocalNodeId).AsSpan().SequenceCompareTo((byte[])Parameters.RemoteNodeId) < 0;
    }

    public InteractiveTxNegotiationStep SendTxSignatures(IReadOnlyList<Witness> localWitnesses,
                                                         CompactSignature? sharedInputSignature)
    {
        if (State != InteractiveTxSessionState.AwaitingTxSignatures
         || (RemoteWitnesses is null && !SendsTxSignaturesFirst()))
            throw new InvalidOperationException($"tx_signatures not allowed in state {State}");
        if (localWitnesses.Count != InputList.Count(i => i.AddedBy == InteractiveTxParty.Local && !i.IsShared))
            throw new InvalidOperationException("One witness per input we added");

        var message = new TxSignaturesMessage(
            new TxSignaturesPayload(Parameters.ChannelId, ConstructedTx!.TxId, localWitnesses.ToList()),
            sharedInputSignature is null ? null : new SharedInputSignatureTlv(sharedInputSignature));
        var next = this with
        {
            State = RemoteWitnesses is null
                        ? InteractiveTxSessionState.TxSignaturesSent
                        : InteractiveTxSessionState.Signed
        };
        return new InteractiveTxNegotiationStep(next, [message], false);
    }

    public InteractiveTxNegotiationStep Abort(string reason)
    {
        if (State is InteractiveTxSessionState.TxSignaturesSent or InteractiveTxSessionState.Signed)
            throw new InvalidOperationException("No tx_abort after our tx_signatures");

        return new InteractiveTxNegotiationStep(this with { State = InteractiveTxSessionState.Aborted, AbortSent = true },
                                                [Abort(Parameters.ChannelId, reason)], false, reason);
    }

    private InteractiveTxNegotiationStep ReceiveSignatures(TxSignaturesMessage signatures)
    {
        if (State is not (InteractiveTxSessionState.AwaitingTxSignatures
                          or InteractiveTxSessionState.TxSignaturesSent))
            return Fail($"tx_signatures in state {State}", "IT-SIG-03");
        if (State == InteractiveTxSessionState.AwaitingTxSignatures && SendsTxSignaturesFirst())
            return Fail("tx_signatures before ours although we send first", "IT-SIG-01");
        if (!((byte[])ConstructedTx!.TxId).AsSpan().SequenceEqual(signatures.Payload.TxId))
            return Fail("tx_signatures for another txid", "IT-SIG-02");
        if (signatures.Payload.Witnesses.Count
         != InputList.Count(i => i.AddedBy == InteractiveTxParty.Remote && !i.IsShared)
         || signatures.Payload.Witnesses.Any(w => w.Length == 0))
            return Fail("wrong witness count or an empty witness", "IT-SIG-02");
        if (InputList.Any(i => i.IsShared) && signatures.SharedInputSignatureTlv is null)
            return Fail("shared_input_signature missing", "IT-SIG-02");

        var next = this with
        {
            RemoteWitnesses = signatures.Payload.Witnesses.ToList(),
            RemoteSharedInputSignature = signatures.SharedInputSignatureTlv?.Signature,
            State = State == InteractiveTxSessionState.TxSignaturesSent
                        ? InteractiveTxSessionState.Signed
                        : InteractiveTxSessionState.AwaitingTxSignatures
        };
        return new InteractiveTxNegotiationStep(next, [], false);
    }

    private InteractiveTxNegotiationStep SendNext()
    {
        if (Pending.IsEmpty)
        {
            var complete = new TxCompleteMessage(new TxCompletePayload(Parameters.ChannelId));
            var afterComplete = this with { IsOurTurn = false, LastSentComplete = true };
            return afterComplete.ReceivedComplete
                       ? afterComplete.CompleteNegotiation([complete])
                       : new InteractiveTxNegotiationStep(afterComplete, [complete], false);
        }

        var pending = Pending.Dequeue(out var action);
        var serialId = NextSerialId;
        var next = this with
        {
            Pending = pending,
            IsOurTurn = false,
            LastSentComplete = false,
            ReceivedComplete = false,
            NextSerialId = serialId + 2
        };

        IChannelMessage message;
        if (action.SharedInput is { } sharedInput)
        {
            message = new TxAddInputMessage(
                new TxAddInputPayload(Parameters.ChannelId, serialId, [], sharedInput.Vout,
                                      InteractiveTransactionConstants.MaxSequence),
                new SharedInputTxIdTlv(sharedInput.TxId));
            next = next with
            {
                InputList = next.InputList.Add(new InteractiveTxInput(
                    serialId, InteractiveTxParty.Local, sharedInput.TxId, sharedInput.Vout,
                    InteractiveTransactionConstants.MaxSequence, sharedInput.Amount, sharedInput.ScriptPubKey, null,
                    true))
            };
        }
        else if (action.Input is { } input)
        {
            message = new TxAddInputMessage(new TxAddInputPayload(Parameters.ChannelId, serialId, input.PrevTx,
                                                                  input.PrevTxVout, input.Sequence));
            next = next with
            {
                InputList = next.InputList.Add(new InteractiveTxInput(
                    serialId, InteractiveTxParty.Local, input.PrevTxId, input.PrevTxVout, input.Sequence,
                    input.Amount, input.ScriptPubKey, input.PrevTx, false))
            };
        }
        else
        {
            var amount = action.SharedOutput ? Parameters.SharedFunding!.SharedOutputAmount : action.Output!.Amount;
            var script = action.SharedOutput
                             ? Parameters.SharedFunding!.SharedOutputScript
                             : action.Output!.ScriptPubKey;
            message = new TxAddOutputMessage(new TxAddOutputPayload(amount, Parameters.ChannelId, script, serialId));
            next = next with
            {
                OutputList = next.OutputList.Add(new InteractiveTxOutput(serialId, InteractiveTxParty.Local, amount,
                                                                         script, action.SharedOutput))
            };
        }

        return new InteractiveTxNegotiationStep(next, [message], false);
    }

    private InteractiveTxNegotiationStep CompleteNegotiation(IReadOnlyList<IChannelMessage> outbound)
    {
        // IT-R-04 (simplified): the peer's inputs cover its outputs, shares included
        var remoteIn = SumAmounts(InputList.Where(i => i.AddedBy == InteractiveTxParty.Remote && !i.IsShared)
                                           .Select(i => i.Amount))
                     + (Parameters.SharedFunding?.RemoteInputShare ?? LightningMoney.Zero);
        var remoteOut = SumAmounts(OutputList.Where(o => o.AddedBy == InteractiveTxParty.Remote && !o.IsShared)
                                             .Select(o => o.Amount))
                      + (Parameters.SharedFunding?.RemoteOutputShare ?? LightningMoney.Zero);
        if (remoteIn < remoteOut)
            return Fail("the peer's inputs do not cover its outputs", "IT-R-04");

        // IT-RBF-01: every previous attempt must be double-spent
        foreach (var previous in Parameters.PreviousAttempts)
        {
            if (!previous.Inputs.Any(p => InputList.Any(i => i.PrevTxId == p.PrevTxId && i.PrevTxVout == p.PrevTxVout)))
                return Fail("the attempt does not double-spend a previous attempt", "IT-RBF-01");
        }

        return new InteractiveTxNegotiationStep(this with { NegotiationDone = true, IsOurTurn = false }, outbound,
                                                true);
    }

    private InteractiveTxInput? CheckInput(TxAddInputMessage message, IPrevTxInspector prevTxInspector,
                                           out string? error)
    {
        var payload = message.Payload;
        error = CheckSerialId(payload.SerialId, InputList.Select(i => i.SerialId));
        if (error is not null)
            return null;
        if (payload.Sequence > InteractiveTransactionConstants.MaxSequence)
        {
            error = "sequence above 0xFFFFFFFD";
            return null;
        }

        InteractiveTxInput input;
        if (message.SharedInputTxIdTlv is { } sharedTxId)
        {
            var spec = Parameters.SharedFunding?.SharedInput;
            if (spec is null || payload.PrevTx.Length != 0 || sharedTxId.FundingTxId != spec.TxId
             || InputList.Any(i => i.IsShared))
            {
                error = "unexpected shared input";
                return null;
            }

            input = new InteractiveTxInput(payload.SerialId, InteractiveTxParty.Remote, spec.TxId, spec.Vout,
                                           payload.Sequence, spec.Amount, spec.ScriptPubKey, null, true);
        }
        else
        {
            var inspection = prevTxInspector.Inspect(payload.PrevTx, payload.PrevTxVout);
            if (!inspection.IsValid || !inspection.IsWitnessProgram)
            {
                error = inspection.FailureReason ?? "prevtx output is not a witness program";
                return null;
            }

            input = new InteractiveTxInput(payload.SerialId, InteractiveTxParty.Remote, inspection.TxId!.Value,
                                           payload.PrevTxVout, payload.Sequence, inspection.Amount!,
                                           inspection.ScriptPubKey!.Value, payload.PrevTx, false);
        }

        if (InputList.Any(i => i.PrevTxId == input.PrevTxId && i.PrevTxVout == input.PrevTxVout))
        {
            error = "duplicate prevtx and vout";
            return null;
        }

        return input;
    }

    private InteractiveTxOutput? CheckOutput(TxAddOutputPayload payload, out string? error)
    {
        error = CheckSerialId(payload.SerialId, OutputList.Select(o => o.SerialId));
        if (error is not null)
            return null;
        if (payload.Amount < s_dustLimit || payload.Amount > s_maxMoney)
        {
            error = "output amount below dust or above MAX_MONEY";
            return null;
        }

        var isShared = Parameters.SharedFunding is { } spec && payload.Script.Equals(spec.SharedOutputScript);
        return new InteractiveTxOutput(payload.SerialId, InteractiveTxParty.Remote, payload.Amount, payload.Script,
                                       isShared);
    }

    private string? CheckSerialId(ulong serialId, IEnumerable<ulong> existing)
    {
        // The peer is the initiator exactly when we are not: the initiator's serial ids are even
        var peerUsesEven = !Parameters.IsInitiator;
        if ((serialId % 2 == 0) != peerUsesEven)
            return "serial_id of the wrong parity";

        return existing.Contains(serialId) ? "duplicate serial_id" : null;
    }

    private LightningMoney SumContributed(InteractiveTxParty party, bool ownsSharedInput)
    {
        var total = SumAmounts(InputList.Where(i => i.AddedBy == party && !i.IsShared).Select(i => i.Amount));
        if (ownsSharedInput && InputList.FirstOrDefault(i => i.IsShared) is { } shared)
            total += shared.Amount;
        return total;
    }

    private static LightningMoney SumAmounts(IEnumerable<LightningMoney> amounts) =>
        amounts.Aggregate(LightningMoney.Zero, (sum, amount) => sum + amount);

    private InteractiveTxNegotiationStep Fail(string reason, string requirementId) =>
        new(this with { State = InteractiveTxSessionState.Aborted, AbortSent = true },
            [Abort(Parameters.ChannelId, reason)], false, reason, requirementId);

    private static TxAbortMessage Abort(ChannelId channelId, string reason) =>
        new(new TxAbortPayload(channelId, System.Text.Encoding.ASCII.GetBytes(reason)));

    private sealed record LocalAction(
        ContributedInput? Input,
        ContributedOutput? Output,
        SharedFundingInput? SharedInput,
        bool SharedOutput);
}