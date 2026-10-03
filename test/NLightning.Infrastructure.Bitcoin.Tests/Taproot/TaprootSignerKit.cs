using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Crypto.Functions;
using Bitcoin.Crypto.Musig2;
using Bitcoin.Services;
using Bitcoin.Signers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;

/// <summary>
/// Two real <see cref="LocalLightningSigner"/>s (Alice and Bob, each with its own channel key) registered for one
/// simple taproot channel: the MuSig2 P2TR funding output of both funding keys, spent by the transactions the tests
/// sign. Alice is the opener.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class TaprootSignerKit
{
    public const ulong CapacitySat = 1_000_000;

    public static readonly ChannelId ChannelId = new(Enumerable.Repeat((byte)0x7a, 32).ToArray());

    public LocalLightningSigner Alice { get; }
    public LocalLightningSigner Bob { get; }
    public ChannelBasepoints AliceBasepoints { get; }
    public ChannelBasepoints BobBasepoints { get; }
    public Transaction FundingTx { get; }
    public TxId FundingTxId { get; }
    public MusigKeyAggregate Aggregate { get; }
    public Musig2Service Musig { get; } = new();

    public TaprootSignerKit(ulong aliceLocalNumber = 0, ulong bobLocalNumber = 0, bool isSimpleTaproot = true,
                            bool register = true, bool isDualFunded = false)
    {
        IsDualFunded = isDualFunded;
        Alice = CreateSigner(0xa1);
        Bob = CreateSigner(0xb0);
        AliceBasepoints = Alice.GetChannelBasepoints(0u);
        BobBasepoints = Bob.GetChannelBasepoints(0u);

        Aggregate = Musig.AggregateTaprootKeyPath(AliceBasepoints.FundingPubKey, BobBasepoints.FundingPubKey);
        FundingTx = CreateFundingTx(Aggregate, 0x01);
        FundingTxId = FundingTx.GetHash().ToBytes();

        if (!register)
            return;

        Alice.RegisterChannel(ChannelId, SigningInfo(alice: true, aliceLocalNumber, isSimpleTaproot));
        Bob.RegisterChannel(ChannelId, SigningInfo(alice: false, bobLocalNumber, isSimpleTaproot));
    }

    /// <summary>Whether the channel is registered as opened with the dual-funded (v2) open.</summary>
    public bool IsDualFunded { get; }

    /// <summary>The funding output (P2TR of the MuSig2 key path) as NBitcoin sees it.</summary>
    public TxOut FundingTxOut => FundingTx.Outputs[0];

    public ChannelSigningInfo SigningInfo(bool alice, ulong localCommitmentNumber = 0, bool isSimpleTaproot = true,
                                          bool dataLossDetected = false) =>
        new(FundingTxId, 0, LightningMoney.Satoshis(CapacitySat),
            alice ? AliceBasepoints.FundingPubKey : BobBasepoints.FundingPubKey,
            alice ? BobBasepoints.FundingPubKey : AliceBasepoints.FundingPubKey, 0,
            alice ? BobBasepoints.HtlcBasepoint : AliceBasepoints.HtlcBasepoint, localCommitmentNumber,
            dataLossDetected)
        {
            IsSimpleTaproot = isSimpleTaproot,
            IsDualFunded = IsDualFunded
        };

    /// <summary>A transaction spending the funding output (a stand-in commitment or closing transaction).</summary>
    public SignedTransaction UnsignedSpend(long outputSat = 990_000, TxId? fundingTxId = null, uint lockTime = 0x20000005)
    {
        var tx = Transaction.Create(Network.Main);
        tx.Version = 2;
        tx.LockTime = new LockTime(lockTime);
        var prevTxId = fundingTxId ?? FundingTxId;
        tx.Inputs.Add(new OutPoint(new uint256((byte[])prevTxId), 0), Script.Empty, WitScript.Empty,
                      new Sequence(0x80000001));
        tx.Outputs.Add(Money.Satoshis(outputSat),
                       new Key(Enumerable.Repeat((byte)0x33, 32).ToArray()).PubKey.WitHash.ScriptPubKey);
        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
    }

    /// <summary>
    /// A second pending funding of the channel registered with both signers: a splice on funding key 1 of each side,
    /// or (key index 0) another attempt of a dual-funded open on the original keys.
    /// </summary>
    public (TxId FundingTxId, Transaction Tx) RegisterPendingFunding(uint keyIndex = 1)
    {
        var aliceKey = Alice.GetFundingPubKey(0u, keyIndex);
        var bobKey = Bob.GetFundingPubKey(0u, keyIndex);
        var tx = CreateFundingTx(Musig.AggregateTaprootKeyPath(aliceKey, bobKey), (byte)(0x02 + keyIndex));
        TxId txId = tx.GetHash().ToBytes();
        Alice.RegisterFunding(ChannelId, Funding(txId, aliceKey, bobKey, keyIndex));
        Bob.RegisterFunding(ChannelId, Funding(txId, bobKey, aliceKey, keyIndex));
        return (txId, tx);
    }

    /// <summary>Script execution of input 0 of a signed spend of the funding output.</summary>
    public static ScriptError? Execute(SignedTransaction signed, TxOut spentOutput)
    {
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        return tx.CreateValidator([spentOutput]).ValidateInput(0).Error;
    }

    public static LocalLightningSigner CreateSigner(byte seedByte)
    {
        var seed = Enumerable.Repeat(seedByte, 32).ToArray();
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetChannelKeyAtIndex(It.IsAny<uint>()))
                  .Returns((uint index) => ExtKey.CreateFromSeed(seed).Derive((int)index, true).ToBytes());
        var utxos = new Mock<IUtxoMemoryRepository>();
        utxos.Setup(u => u.GetLockedUtxosForChannel(It.IsAny<ChannelId>())).Returns([]);
        return new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
                                        NullLogger<LocalLightningSigner>.Instance, new NodeOptions(),
                                        keyManager.Object, utxos.Object);
    }

    private static ChannelFunding Funding(TxId txId, Domain.Crypto.ValueObjects.CompactPubKey local,
                                          Domain.Crypto.ValueObjects.CompactPubKey remote, uint keyIndex) =>
        new(txId, 0, CapacitySat, local, remote, keyIndex, 0, 0,
            keyIndex == 0 ? ChannelFundingKind.Initial : ChannelFundingKind.Splice, ChannelFundingStatus.Pending);

    private static Transaction CreateFundingTx(MusigKeyAggregate aggregate, byte inputSeed)
    {
        var tx = Transaction.Create(Network.Main);
        tx.Version = 2;
        tx.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(inputSeed, 32).ToArray()), 0));
        tx.Outputs.Add(Money.Satoshis(CapacitySat), new Script(aggregate.GetTaprootScriptPubKey()));
        return tx;
    }
}