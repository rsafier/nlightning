namespace NLightning.Application.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Interfaces;
using Models;

/// <summary>
/// The production <see cref="IInteractiveTxEngine"/>: every call goes to the pure Domain
/// <see cref="InteractiveTxSession"/> (lane IT-A), which owns the BOLT 2 rules.
/// </summary>
public sealed class DomainInteractiveTxEngine : IInteractiveTxEngine
{
    /// <inheritdoc />
    public IInteractiveTxNegotiation Create(InteractiveTxSessionParameters parameters) =>
        new SessionNegotiation(InteractiveTxSession.Create(parameters));

    /// <inheritdoc />
    public IInteractiveTxNegotiation Restore(InteractiveTxSessionModel model,
                                             InteractiveTxSessionParameters parameters) =>
        new SessionNegotiation(InteractiveTxSession.Restore(model, parameters));

    private sealed class SessionNegotiation(InteractiveTxSession session) : IInteractiveTxNegotiation
    {
        public InteractiveTxSessionParameters Parameters => session.Parameters;
        public InteractiveTxSessionState State => session.State;
        public IReadOnlyList<InteractiveTxInput> Inputs => session.Inputs;
        public IReadOnlyList<InteractiveTxOutput> Outputs => session.Outputs;
        public ConstructedInteractiveTx? ConstructedTx => session.ConstructedTx;
        public IReadOnlyList<Witness>? RemoteWitnesses => session.RemoteWitnesses;
        public CompactSignature? RemoteSharedInputSignature => session.RemoteSharedInputSignature;

        public MusigPartialSignatureWithNonce? RemoteSharedInputPartialSignature =>
            session.RemoteSharedInputPartialSignature;

        public InteractiveTxNegotiationStep Start() => Wrap(session.Start());

        public InteractiveTxNegotiationStep Receive(IChannelMessage message, IPrevTxInspector prevTxInspector) =>
            Wrap(session.Receive(message, prevTxInspector));

        public IInteractiveTxNegotiation WithConstructedTransaction(ConstructedInteractiveTx transaction) =>
            new SessionNegotiation(session.WithConstructedTransaction(transaction));

        public IInteractiveTxNegotiation OnCommitmentSignedReceived() =>
            new SessionNegotiation(session.OnCommitmentSignedReceived());

        public bool SendsTxSignaturesFirst() => session.SendsTxSignaturesFirst();

        public InteractiveTxNegotiationStep SendTxSignatures(IReadOnlyList<Witness> localWitnesses,
                                                             CompactSignature? sharedInputSignature) =>
            Wrap(session.SendTxSignatures(localWitnesses, sharedInputSignature));

        public InteractiveTxNegotiationStep SendTxSignatures(IReadOnlyList<Witness> localWitnesses,
                                                             CompactSignature? sharedInputSignature,
                                                             MusigPartialSignatureWithNonce?
                                                                 sharedInputPartialSignature) =>
            Wrap(session.SendTxSignatures(localWitnesses, sharedInputSignature, sharedInputPartialSignature));

        public InteractiveTxNegotiationStep Abort(string reason) => Wrap(session.Abort(reason));

        private static InteractiveTxNegotiationStep Wrap(InteractiveTxStepResult result) =>
            new(new SessionNegotiation(result.Next), result.Outbound, result.NegotiationComplete, result.AbortReason,
                result.RequirementId);
    }
}