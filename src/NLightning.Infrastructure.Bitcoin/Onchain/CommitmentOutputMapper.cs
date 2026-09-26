using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Onchain;

using Builders.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Interfaces;
using Outputs;

/// <summary>
/// Rebuilds a commitment with the same factory and builder that produced (and signed) it, and maps each output to a
/// <see cref="CommitmentOutputDescriptor"/> (BOLT 5 plan §3.3, task O2-T4).
/// </summary>
/// <remarks>
/// When the transaction on chain has the rebuilt txid, the builder's own output map is used (HTLC outputs by vout,
/// the others by script). Otherwise (risk §8.1: a rebuild that differs, e.g. a revoked commitment whose spec is not
/// exact) each output on chain is matched by its scriptPubKey; outputs sharing a script (identical HTLCs) are matched
/// by amount, then in order. HTLCs of the spec without a mapped output are reported in
/// <see cref="CommitmentOutputMap.HtlcsWithoutOutput"/>.
/// </remarks>
public sealed class CommitmentOutputMapper : ICommitmentOutputMapper
{
    private const ushort AnchorCsvDelay = 16;

    private readonly ICommitmentTransactionModelFactory _modelFactory;
    private readonly ICommitmentTransactionBuilder _commitmentBuilder;

    public CommitmentOutputMapper(ICommitmentTransactionModelFactory modelFactory,
                                  ICommitmentTransactionBuilder commitmentBuilder)
    {
        _modelFactory = modelFactory;
        _commitmentBuilder = commitmentBuilder;
    }

    /// <inheritdoc />
    public CommitmentOutputMap Map(ChannelModel channel, CommitmentTxSpec spec, CommitmentCase commitmentCase,
                                   ulong number, CompactPubKey? remotePerCommitmentPoint, ChainTx? onChain = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spec);

        var model = commitmentCase switch
        {
            CommitmentCase.Local => _modelFactory.CreateCommitmentTransactionModel(
                channel, spec, CommitmentSide.Local, number),
            CommitmentCase.Remote or CommitmentCase.Revoked => _modelFactory.CreateCommitmentTransactionModel(
                channel, spec, CommitmentSide.Remote, number,
                remotePerCommitmentPoint
             ?? throw new ArgumentNullException(nameof(remotePerCommitmentPoint),
                                                "A peer commitment needs its per-commitment point")),
            _ => throw new ArgumentOutOfRangeException(nameof(commitmentCase), commitmentCase, null)
        };

        return Map(model, spec.Htlcs, commitmentCase, onChain);
    }

    /// <inheritdoc />
    public CommitmentOutputMap Map(CommitmentTransactionModel model, IEnumerable<Htlc> committedHtlcs,
                                   CommitmentCase commitmentCase, ChainTx? onChain = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(committedHtlcs);
        if (commitmentCase is not (CommitmentCase.Local or CommitmentCase.Remote or CommitmentCase.Revoked))
            throw new ArgumentOutOfRangeException(nameof(commitmentCase), commitmentCase, null);

        var perCommitmentPoint = model.PerCommitmentPoint
                              ?? throw new ArgumentException(
                                     "The commitment model has no per-commitment point; build it with the factory",
                                     nameof(model));

        var built = _commitmentBuilder.BuildWithOutputMap(model);
        var expectedTxId = built.Transaction.TxId;
        var candidates = CreateCandidates(model, commitmentCase);
        var txIdMatched = onChain is null || onChain.TxId == expectedTxId;
        var commitmentTxId = onChain?.TxId ?? expectedTxId;

        var descriptors = new List<CommitmentOutputDescriptor>();
        var unmapped = new List<uint>();
        if (txIdMatched)
            MapBuilt(model, built, candidates, commitmentCase, commitmentTxId, descriptors);
        else
            MapByScript(model, onChain!, candidates, commitmentCase, descriptors, unmapped);

        var mappedHtlcs = descriptors.Where(d => d.Htlc is not null)
                                     .Select(d => new HtlcKey(d.Htlc!.Value.Direction, d.Htlc.Value.Id))
                                     .ToHashSet();
        var withoutOutput = committedHtlcs.Select(ToSpecHtlc)
                                          .Where(h => !mappedHtlcs.Contains(new HtlcKey(h.Direction, h.Id)))
                                          .OrderBy(h => h.Direction)
                                          .ThenBy(h => h.Id)
                                          .ToList();

        return new CommitmentOutputMap(commitmentCase, model.Number, perCommitmentPoint, expectedTxId,
                                       onChain?.TxId, descriptors.OrderBy(d => d.Vout).ToList(), withoutOutput,
                                       unmapped);
    }

    /// <inheritdoc />
    public IReadOnlyList<CommitmentOutputDescriptor> FindPaymentToRemote(ChainTx onChain,
                                                                         CompactPubKey ourPaymentBasepoint,
                                                                         bool hasAnchors)
    {
        ArgumentNullException.ThrowIfNull(onChain);

        // The amount does not change the script
        var toRemote = new ToRemoteOutput(LightningMoney.Zero, hasAnchors, new PubKey(ourPaymentBasepoint));
        var scriptPubKey = toRemote.ScriptPubKey.ToBytes();
        var witnessScript = hasAnchors ? toRemote.RedeemScript.ToBytes() : null;

        var found = new List<CommitmentOutputDescriptor>();
        for (var vout = 0; vout < onChain.Outputs.Count; vout++)
        {
            var output = onChain.Outputs[vout];
            if (output.ScriptPubKey.AsSpan().SequenceEqual(scriptPubKey))
                found.Add(new CommitmentOutputDescriptor((uint)vout, output.AmountSat,
                                                         OutputDescriptorKind.PaymentToRemote, output.ScriptPubKey,
                                                         witnessScript, null, (ushort)(hasAnchors ? 1 : 0),
                                                         hasAnchors));
        }

        return found;
    }

    private static void MapBuilt(CommitmentTransactionModel model, CommitmentTransactionBuildResult built,
                                 IReadOnlyList<Candidate> candidates, CommitmentCase commitmentCase,
                                 TxId commitmentTxId, List<CommitmentOutputDescriptor> descriptors)
    {
        var tx = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        var htlcVouts = built.HtlcOutputsInTxOrder.ToDictionary(h => h.Vout, h => h.Output);
        var used = new HashSet<Candidate>();

        for (var vout = 0u; vout < tx.Outputs.Count; vout++)
        {
            var txOut = tx.Outputs[(int)vout];
            Candidate? candidate;
            if (htlcVouts.TryGetValue(vout, out var htlcOutput))
                candidate = candidates.First(c => ReferenceEquals(c.HtlcOutput, htlcOutput));
            else
            {
                var script = txOut.ScriptPubKey.ToBytes();
                candidate = candidates.FirstOrDefault(c => c.HtlcOutput is null && !used.Contains(c)
                                                        && c.ScriptPubKey.AsSpan().SequenceEqual(script));
            }

            if (candidate is null)
                throw new InvalidOperationException($"Rebuilt commitment output {vout} matches no model output");

            used.Add(candidate);
            descriptors.Add(ToDescriptor(model, candidate, vout, (ulong)txOut.Value.Satoshi, commitmentCase,
                                         commitmentTxId));
        }
    }

    private static void MapByScript(CommitmentTransactionModel model, ChainTx onChain,
                                    IReadOnlyList<Candidate> candidates, CommitmentCase commitmentCase,
                                    List<CommitmentOutputDescriptor> descriptors, List<uint> unmapped)
    {
        var used = new HashSet<Candidate>();
        for (var vout = 0u; vout < onChain.Outputs.Count; vout++)
        {
            var output = onChain.Outputs[(int)vout];
            var sameScript = candidates.Where(c => !used.Contains(c)
                                                && c.ScriptPubKey.AsSpan().SequenceEqual(output.ScriptPubKey))
                                       .ToList();
            var candidate = sameScript.FirstOrDefault(c => c.AmountSat == output.AmountSat)
                         ?? sameScript.FirstOrDefault();
            if (candidate is null)
            {
                unmapped.Add(vout);
                continue;
            }

            used.Add(candidate);
            descriptors.Add(ToDescriptor(model, candidate, vout, output.AmountSat, commitmentCase, onChain.TxId));
        }
    }

    private static CommitmentOutputDescriptor ToDescriptor(CommitmentTransactionModel model, Candidate candidate,
                                                           uint vout, ulong amountSat, CommitmentCase commitmentCase,
                                                           TxId commitmentTxId)
    {
        HtlcTransactionModel? secondLevel = null;
        SpecHtlc? htlc = null;
        if (candidate.HtlcOutput is { } htlcOutput)
        {
            htlc = ToSpecHtlc(htlcOutput.Htlc);

            // Our own commitment's HTLC outputs are spent by our pre-signed HTLC-timeout/success transactions
            if (commitmentCase == CommitmentCase.Local)
                secondLevel = HtlcTransactionModelFactory.CreateHtlcTransactionModel(model, commitmentTxId, htlcOutput,
                                                                                      vout);
        }

        return new CommitmentOutputDescriptor(vout, amountSat, candidate.Kind, candidate.ScriptPubKey,
                                              candidate.WitnessScript, htlc, candidate.CsvDelay, model.HasAnchors,
                                              secondLevel);
    }

    /// <summary>
    /// Every output the model can produce, converted exactly as <c>CommitmentTransactionBuilder</c> converts it.
    /// </summary>
    private static List<Candidate> CreateCandidates(CommitmentTransactionModel model, CommitmentCase commitmentCase)
    {
        // The builder derives the anchor script forms from the presence of anchor outputs, not from HasAnchors
        var hasAnchorOutputs = model.LocalAnchorOutput is not null || model.RemoteAnchorOutput is not null;
        var htlcCsv = (ushort)(model.HasAnchors ? 1 : 0);
        var candidates = new List<Candidate>();

        if (model.ToLocalOutput is { } toLocal)
        {
            var output = new ToLocalOutput(toLocal.Amount, new PubKey(toLocal.LocalDelayedPaymentPubKey),
                                           new PubKey(toLocal.RevocationPubKey), toLocal.ToSelfDelay);
            var kind = commitmentCase switch
            {
                CommitmentCase.Local => OutputDescriptorKind.DelayedToLocal,
                CommitmentCase.Revoked => OutputDescriptorKind.RevokedToLocal,
                _ => OutputDescriptorKind.PeerOutput
            };
            candidates.Add(new Candidate(output, kind, null, toLocal.ToSelfDelay, true));
        }

        if (model.ToRemoteOutput is { } toRemote)
        {
            var output = new ToRemoteOutput(toRemote.Amount, hasAnchorOutputs,
                                            new PubKey(toRemote.RemotePaymentPubKey));
            var kind = commitmentCase == CommitmentCase.Local
                           ? OutputDescriptorKind.PeerOutput
                           : OutputDescriptorKind.PaymentToRemote;
            candidates.Add(new Candidate(output, kind, null, htlcCsv, hasAnchorOutputs));
        }

        if (model.LocalAnchorOutput is { } holderAnchor)
            candidates.Add(new Candidate(new ToAnchorOutput(holderAnchor.Amount, new PubKey(holderAnchor.FundingPubKey)),
                                         commitmentCase == CommitmentCase.Local
                                             ? OutputDescriptorKind.OurAnchor
                                             : OutputDescriptorKind.PeerAnchor, null, AnchorCsvDelay, true));

        if (model.RemoteAnchorOutput is { } counterpartyAnchor)
            candidates.Add(new Candidate(new ToAnchorOutput(counterpartyAnchor.Amount,
                                                            new PubKey(counterpartyAnchor.FundingPubKey)),
                                         commitmentCase == CommitmentCase.Local
                                             ? OutputDescriptorKind.PeerAnchor
                                             : OutputDescriptorKind.OurAnchor, null, AnchorCsvDelay, true));

        foreach (var offered in model.OfferedHtlcOutputs)
        {
            var output = new OfferedHtlcOutput(offered.Amount, offered.CltvExpiry, hasAnchorOutputs,
                                               new PubKey(offered.LocalHtlcPubKey), offered.PaymentHash,
                                               new PubKey(offered.RemoteHtlcPubKey),
                                               new PubKey(offered.RevocationPubKey));
            var kind = commitmentCase switch
            {
                CommitmentCase.Local => OutputDescriptorKind.LocalOfferedHtlc,
                CommitmentCase.Remote => OutputDescriptorKind.RemoteOfferedHtlc,
                _ => OutputDescriptorKind.RevokedHtlc
            };
            candidates.Add(new Candidate(output, kind, offered, htlcCsv, true));
        }

        foreach (var received in model.ReceivedHtlcOutputs)
        {
            var output = new ReceivedHtlcOutput(received.Amount, received.CltvExpiry, hasAnchorOutputs,
                                                new PubKey(received.LocalHtlcPubKey), received.PaymentHash,
                                                new PubKey(received.RemoteHtlcPubKey),
                                                new PubKey(received.RevocationPubKey));
            var kind = commitmentCase switch
            {
                CommitmentCase.Local => OutputDescriptorKind.LocalReceivedHtlc,
                CommitmentCase.Remote => OutputDescriptorKind.RemoteReceivedHtlc,
                _ => OutputDescriptorKind.RevokedHtlc
            };
            candidates.Add(new Candidate(output, kind, received, htlcCsv, true));
        }

        return candidates;
    }

    private static SpecHtlc ToSpecHtlc(Htlc htlc) =>
        new(htlc.Direction, htlc.Id, htlc.Amount.MilliSatoshi, htlc.PaymentHash, htlc.CltvExpiry);

    private sealed class Candidate
    {
        public OutputDescriptorKind Kind { get; }
        public HtlcOutputInfo? HtlcOutput { get; }
        public ushort CsvDelay { get; }
        public byte[] ScriptPubKey { get; }
        public byte[]? WitnessScript { get; }
        public ulong AmountSat { get; }

        public Candidate(BaseOutput output, OutputDescriptorKind kind, HtlcOutputInfo? htlcOutput, ushort csvDelay,
                         bool isWitnessScriptHash)
        {
            Kind = kind;
            HtlcOutput = htlcOutput;
            CsvDelay = csvDelay;
            ScriptPubKey = output.ScriptPubKey.ToBytes();
            WitnessScript = isWitnessScriptHash ? output.RedeemScript.ToBytes() : null;
            AmountSat = (ulong)output.Amount.Satoshi;
        }
    }
}