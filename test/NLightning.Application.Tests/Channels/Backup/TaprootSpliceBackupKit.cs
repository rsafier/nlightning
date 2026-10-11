using Microsoft.Extensions.DependencyInjection;
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
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Outputs;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// A simple taproot channel spliced twice on chain (NL-1059), with real keys: ours from a real
/// <see cref="LocalLightningSigner"/> (<c>m/0'</c>, then <c>m/0'/1'</c> and <c>m/0'/2'</c>), the peer's from NBitcoin
/// keys that it rotates at every splice. Every funding output is the MuSig2 P2TR of <c>KeyAgg(ours, the peer's)</c> and
/// every spend of one is a 64-byte key-path witness, so no witness names a key. <see cref="Splice1"/> spends the
/// funding into a P2TR change output (vout 0) and the new funding (vout 1); <see cref="Splice2"/> spends that into the
/// next funding (vout 0) and a P2TR change output (vout 1); the peer's commitment (<see cref="CommitmentOn"/>) pays our
/// taproot <c>to_remote</c>, keyed to our payment basepoint.
/// </summary>
internal sealed class TaprootSpliceBackupKit
{
    public const uint ChannelKeyIndex = 5;
    public const uint FundingHeight = 500;
    public const uint Splice1Height = 600;
    public const uint Splice2Height = 620;
    public const uint CommitmentHeight = 650;
    public const uint SpliceTransactionIndex = 2;
    public const long FundingSat = 1_000_000;
    public const long Splice1Sat = 1_100_000;
    public const long Splice2Sat = 900_000;

    private readonly Key[] _remoteKeys =
    [
        new(Enumerable.Repeat((byte)0x51, 32).ToArray()), new(Enumerable.Repeat((byte)0x52, 32).ToArray()),
        new(Enumerable.Repeat((byte)0x53, 32).ToArray())
    ];

    public TaprootSpliceBackupKit(bool peerRotatesItsKey = true)
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
        var services = new ServiceCollection();
        services.AddBitcoinInfrastructure();
        Musig2 = services.BuildServiceProvider().GetRequiredService<IMusig2Service>();
        PeerRotatesItsKey = peerRotatesItsKey;

        Funding = SpliceBackupKit.NewTransaction(0);
        Funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        Funding.Outputs.Add(new TxOut(Money.Satoshis(FundingSat), FundingScript(0)));

        Splice1 = SpliceBackupKit.NewTransaction(Splice1Height - 1);
        Splice1.Inputs.Add(KeyPathInput(new OutPoint(Funding, 0)));
        Splice1.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(new string('2', 64)), 3)) { Sequence = 0xFFFFFFFD });
        Splice1.Outputs.Add(new TxOut(Money.Satoshis(40_000), TaprootChange(0x61)));
        Splice1.Outputs.Add(new TxOut(Money.Satoshis(Splice1Sat), FundingScript(1)));

        Splice2 = SpliceBackupKit.NewTransaction(Splice2Height - 1);
        Splice2.Inputs.Add(KeyPathInput(new OutPoint(Splice1, 1)));
        Splice2.Outputs.Add(new TxOut(Money.Satoshis(Splice2Sat), FundingScript(2)));
        Splice2.Outputs.Add(new TxOut(Money.Satoshis(190_000), TaprootChange(0x62)));
    }

    public Mock<ISecureKeyManager> KeyManager { get; }
    public LocalLightningSigner Signer { get; }
    public ChannelBasepoints Basepoints { get; }
    public SignerChannelFundingKeySource KeySource { get; }
    public IMusig2Service Musig2 { get; }
    public bool PeerRotatesItsKey { get; }
    public Transaction Funding { get; }
    public Transaction Splice1 { get; }
    public Transaction Splice2 { get; }

    public TxId FundingTxId => new(Funding.GetHash().ToBytes());
    public TxId Splice1TxId => new(Splice1.GetHash().ToBytes());
    public TxId Splice2TxId => new(Splice2.GetHash().ToBytes());
    public OutPoint Splice1FundingOutPoint => new(Splice1, 1);
    public OutPoint Splice2FundingOutPoint => new(Splice2, 0);

    /// <summary>Our funding key <paramref name="index"/>, from the real signer.</summary>
    public CompactPubKey LocalFundingKey(uint index) => Signer.GetFundingPubKey(ChannelKeyIndex, index);

    /// <summary>The peer's funding key of funding <paramref name="index"/> (the same one when it does not rotate).</summary>
    public CompactPubKey RemoteFundingKey(uint index) =>
        new(_remoteKeys[PeerRotatesItsKey ? index : 0].PubKey.ToBytes());

    /// <summary>The backup entry of the channel before the first splice.</summary>
    public ChannelBackupEntry PreSpliceEntry()
    {
        var data = new BackupTestData();
        var template = ChannelBackupService.CreateEntry(data.AddChannel(1, simpleTaproot: true), data.Peers[0]);
        return template with
        {
            KeyIndex = ChannelKeyIndex,
            LocalFundingPubKey = LocalFundingKey(0),
            LocalPaymentBasepoint = Basepoints.PaymentBasepoint,
            RemoteFundingPubKey = RemoteFundingKey(0),
            FundingTxId = FundingTxId,
            FundingOutputIndex = 0,
            CapacitySat = FundingSat,
            FundingHeight = FundingHeight,
            ShortChannelId = new ShortChannelId(FundingHeight, 1, 0),
            LocalFundingKeyIndex = 0,
            PendingFundings = []
        };
    }

    /// <summary>The peer's commitment spending <paramref name="funding"/>: it pays our taproot <c>to_remote</c>
    /// unless <paramref name="paysUs"/> is false.</summary>
    public Transaction CommitmentOn(OutPoint funding, bool paysUs = true)
    {
        var commitment = SpliceBackupKit.NewTransaction(0x20_00_01_23);
        commitment.Inputs.Add(KeyPathInput(funding, 0x80_00_04_56));
        commitment.Outputs.Add(new TxOut(Money.Satoshis(500_000), TaprootChange(0x71)));
        commitment.Outputs.Add(new TxOut(Money.Satoshis(390_000),
                                         paysUs
                                             ? new Script(new TaprootToRemoteOutput(LightningMoney.Zero,
                                                                                    new PubKey(
                                                                                        Basepoints.PaymentBasepoint))
                                                         .BitcoinScriptPubKey)
                                             : TaprootChange(0x72)));
        return commitment;
    }

    /// <summary>The spend of the backed-up funding by the first splice, as the chain monitor reports it.</summary>
    public OutpointSpentEventArgs Splice1Spend(ChannelBackupEntry entry) =>
        new(entry.ChannelId, new SignedTransaction(Splice1TxId, Splice1.ToBytes()), Splice1Height,
            SpliceTransactionIndex, FundingTxId, 0, new Hash(new byte[32]));

    /// <summary>The spend of the first splice's funding by the second splice.</summary>
    public OutpointSpentEventArgs Splice2Spend(ChannelBackupEntry entry) =>
        new(entry.ChannelId, new SignedTransaction(Splice2TxId, Splice2.ToBytes()), Splice2Height,
            SpliceTransactionIndex, Splice1TxId, 1, new Hash(new byte[32]));

    /// <summary>The block at <paramref name="height"/> holding a filler then <paramref name="transaction"/> (index 2).</summary>
    public static Block BlockAt(uint height, Transaction transaction)
    {
        var filler = SpliceBackupKit.NewTransaction(0);
        filler.Inputs.Add(new TxIn(new OutPoint(uint256.One, height)));
        filler.Outputs.Add(new TxOut(Money.Satoshis(1_000), new Key().PubKey.WitHash.ScriptPubKey));
        return SpliceBackupKit.BlockWith(height, filler, transaction);
    }

    private Script FundingScript(uint index) =>
        new(SpliceFundingScripts.CreateScriptPubKey(LocalFundingKey(index), RemoteFundingKey(index), true, Musig2));

    private static Script TaprootChange(byte seed) =>
        new Key(Enumerable.Repeat(seed, 32).ToArray()).PubKey.GetTaprootFullPubKey().ScriptPubKey;

    /// <summary>A key-path spend: one 64-byte signature, no key.</summary>
    private static TxIn KeyPathInput(OutPoint outPoint, uint sequence = 0xFFFFFFFD) =>
        new(outPoint) { Sequence = sequence, WitScript = new WitScript([new byte[64]]) };
}