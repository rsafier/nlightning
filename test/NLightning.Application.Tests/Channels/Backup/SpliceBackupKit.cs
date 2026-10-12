using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Application.Channels.Splicing;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// A channel spliced on chain, with real keys (lane SP2-E): our channel keys come from a real
/// <see cref="LocalLightningSigner"/> (funding key <c>m/0'</c> for index 0, <c>m/0'/i'</c> after a splice), the peer's
/// from NBitcoin keys. <see cref="Funding"/> pays the 2-of-2 of our key 0 and the peer's key; <see cref="Splice"/>
/// spends it into the 2-of-2 of our key 1 and the peer's key <see cref="SpliceRemoteKey"/> (vout 1, after a change
/// output); <see cref="Commitment"/> is the peer's BOLT 3 commitment spending the splice's funding output.
/// </summary>
internal sealed class SpliceBackupKit
{
    public const uint ChannelKeyIndex = 5;
    public const uint FundingHeight = 500;
    public const uint SpliceHeight = 600;
    public const uint SpliceTransactionIndex = 2;
    public const ushort SpliceOutputIndex = 1;
    public const long FundingSat = 1_000_000;
    public const long SpliceSat = 1_100_000;

    private readonly Key _remoteKey = new(Enumerable.Repeat((byte)0x51, 32).ToArray());
    private readonly Key _rotatedRemoteKey = new(Enumerable.Repeat((byte)0x52, 32).ToArray());

    public SpliceBackupKit(bool peerRotatesItsKey = false)
    {
        var rootKey = new ExtKey(new Key(Enumerable.Repeat((byte)0x33, 32).ToArray()), new byte[32]);
        KeyManager = new Mock<ISecureKeyManager>();
        KeyManager.Setup(k => k.GetChannelKeyAtIndex(It.IsAny<uint>()))
                  .Returns((uint index) => (ExtPrivKey)rootKey.Derive((int)index, true).ToBytes());
        Signer = new LocalLightningSigner(new FundingOutputBuilder(), Mock.Of<IKeyDerivationService>(),
                                          NullLogger<LocalLightningSigner>.Instance,
                                          new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest },
                                          KeyManager.Object, Mock.Of<IUtxoMemoryRepository>());
        Basepoints = Signer.GetChannelBasepoints(ChannelKeyIndex);
        KeySource = new SignerChannelFundingKeySource(Signer);

        RemoteFundingKey = new CompactPubKey(_remoteKey.PubKey.ToBytes());
        SpliceRemoteKey = peerRotatesItsKey ? new CompactPubKey(_rotatedRemoteKey.PubKey.ToBytes()) : RemoteFundingKey;

        Funding = NewTransaction(0);
        Funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        Funding.Outputs.Add(new TxOut(Money.Satoshis(FundingSat), FundingScript(LocalFundingKey(0), RemoteFundingKey)));

        Splice = NewTransaction(SpliceHeight - 1);
        Splice.Inputs.Add(new TxIn(new OutPoint(Funding, 0)) { Sequence = 0xFFFFFFFD });
        Splice.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(new string('2', 64)), 3)) { Sequence = 0xFFFFFFFD });
        Splice.Outputs.Add(new TxOut(Money.Satoshis(40_000), new Key().PubKey.WitHash.ScriptPubKey));
        Splice.Outputs.Add(new TxOut(Money.Satoshis(SpliceSat), FundingScript(LocalFundingKey(1), SpliceRemoteKey)));
        Splice.Inputs[0].WitScript = FundingWitness(LocalFundingKey(0), RemoteFundingKey);

        Commitment = NewTransaction(0x20_00_01_23);
        Commitment.Inputs.Add(new TxIn(new OutPoint(Splice, SpliceOutputIndex)) { Sequence = 0x80_00_04_56 });
        Commitment.Outputs.Add(new TxOut(Money.Satoshis(700_000), new Key().PubKey.WitHash.ScriptPubKey));
        Commitment.Outputs.Add(new TxOut(Money.Satoshis(399_000), new Key().PubKey.WitHash.ScriptPubKey));
        Commitment.Inputs[0].WitScript = FundingWitness(LocalFundingKey(1), SpliceRemoteKey);
    }

    public Mock<ISecureKeyManager> KeyManager { get; }
    public LocalLightningSigner Signer { get; }
    public ChannelBasepoints Basepoints { get; }
    public SignerChannelFundingKeySource KeySource { get; }
    public CompactPubKey RemoteFundingKey { get; }
    public CompactPubKey SpliceRemoteKey { get; }
    public Transaction Funding { get; }
    public Transaction Splice { get; }
    public Transaction Commitment { get; }

    public TxId FundingTxId => new(Funding.GetHash().ToBytes());
    public TxId SpliceTxId => new(Splice.GetHash().ToBytes());
    public OutPoint FundingOutPoint => new(Funding, 0);
    public OutPoint SpliceOutPoint => new(Splice, SpliceOutputIndex);

    /// <summary>Our funding key <paramref name="index"/> of the channel, from the real signer.</summary>
    public CompactPubKey LocalFundingKey(uint index) => Signer.GetFundingPubKey(ChannelKeyIndex, index);

    /// <summary>The backup entry of the channel before the splice (funding key index 0).</summary>
    public ChannelBackupEntry PreSpliceEntry(bool withPendingSplice = false)
    {
        var data = new BackupTestData();
        var template = ChannelBackupService.CreateEntry(data.AddChannel(1, anchors: true), data.Peers[0]);
        return template with
        {
            KeyIndex = ChannelKeyIndex,
            LocalFundingPubKey = LocalFundingKey(0),
            LocalPaymentBasepoint = Basepoints.PaymentBasepoint,
            RemoteFundingPubKey = RemoteFundingKey,
            FundingTxId = FundingTxId,
            FundingOutputIndex = 0,
            CapacitySat = FundingSat,
            FundingHeight = FundingHeight,
            ShortChannelId = new ShortChannelId(FundingHeight, 1, 0),
            LocalFundingKeyIndex = 0,
            PendingFundings = withPendingSplice
                                  ?
                                  [
                                      new ChannelBackupFunding(SpliceTxId, SpliceOutputIndex, SpliceSat, 1,
                                                               LocalFundingKey(1), SpliceRemoteKey)
                                  ]
                                  : []
        };
    }

    /// <summary>The backup entry of the channel after the splice locked (funding key index 1).</summary>
    public ChannelBackupEntry PostSpliceEntry() =>
        PreSpliceEntry() with
        {
            FundingTxId = SpliceTxId,
            FundingOutputIndex = SpliceOutputIndex,
            CapacitySat = SpliceSat,
            FundingHeight = SpliceHeight,
            ShortChannelId = new ShortChannelId(SpliceHeight, SpliceTransactionIndex, SpliceOutputIndex),
            LocalFundingKeyIndex = 1,
            LocalFundingPubKey = LocalFundingKey(1),
            RemoteFundingPubKey = SpliceRemoteKey
        };

    /// <summary>The spend of the pre-splice funding by the splice, as the chain monitor reports it.</summary>
    public OutpointSpentEventArgs SpliceSpend(ChannelBackupEntry entry) =>
        new(entry.ChannelId, new SignedTransaction(SpliceTxId, Splice.ToBytes()), SpliceHeight,
            SpliceTransactionIndex, FundingTxId, 0, new Hash(new byte[32]));

    /// <summary>A block at <paramref name="height"/> holding <paramref name="transactions"/> after a coinbase.</summary>
    public static Block BlockWith(uint height, params Transaction[] transactions)
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Header.HashPrevBlock = new uint256(height);
        var coinbase = Network.RegTest.CreateTransaction();
        coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue),
                                     new Script(Op.GetPushOp(height))));
        coinbase.Outputs.Add(new TxOut(Money.Coins(50), new Key().PubKey.WitHash.ScriptPubKey));
        block.AddTransaction(coinbase);
        foreach (var transaction in transactions)
            block.AddTransaction(transaction);
        return block;
    }

    /// <summary>An empty version 2 transaction with <paramref name="lockTime"/>.</summary>
    public static Transaction NewTransaction(uint lockTime)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Version = 2;
        transaction.LockTime = lockTime;
        return transaction;
    }

    private static Script FundingScript(CompactPubKey local, CompactPubKey remote) =>
        new(SpliceFundingScripts.Create(local, remote).ScriptPubKey);

    /// <summary>A 2-of-2 funding input's witness (dummy signatures, the real witness script).</summary>
    private static WitScript FundingWitness(CompactPubKey local, CompactPubKey remote)
    {
        var (_, witnessScript) = SpliceFundingScripts.Create(local, remote);
        var signature = new byte[71];
        signature[0] = 0x30;
        return new WitScript([[], signature, signature, (byte[])witnessScript]);
    }
}