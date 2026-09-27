using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// Splicing plan SP1-C-T1..T3: per-funding keys (D5), per-funding commitment signatures, the shared-input signature
/// behind SP-I1 and the per-funding S1 rule SP-I4, with BOLT 3 Appendix C as the channel's initial funding (node A is
/// us, node B the peer).
/// </summary>
public class LocalLightningSignerSplicingTests
{
    /// <summary>BOLT 3 Appendix C "simple commitment tx with no HTLCs", fully signed (node A holds it).</summary>
    private const string SignedCommitTx0Hex =
        "02000000000101bef67e4e2fb9ddeeb3461973cd4c62abb35050b1add772995b820b584a488489000000000038b02b8002c0c62d0000000000160014cc1b07838e387deacd0e5232e1e8b49f4c29e48454a56a00000000002200204adb4e2f00643db396dd120d4e7dc17625f5f2c11a40d857accc862d6b7dd80e04004730440220616210b2cc4d3afb601013c373bbd8aac54febd9f15400379a8cb65ce7deca60022034236c010991beb7ff770510561ae8dc885b8d38d1947248c38f2ae05564714201483045022100c3127b33dcc741dd6b05b1e63cbd1a9a7d816f37af9b6756fa2376b056f032370220408b96279808fe57eb7e463710804cdf4f108388bc5cf722d8c848d2c7f9f3b001475221023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb21030e9f7b623d2ccc7c9bd44d66d5ce21ce504c0acf6385a132cec6d3c39fa711c152ae3e195220";

    /// <summary>BOLT 3 Appendix C <c>INTERNAL: remote_funding_privkey</c> (node B), without the compression byte.</summary>
    private static readonly Key s_nodeBFundingKey =
        new(Convert.FromHexString("1552dfba4f6cf29a62a0af13c8d6981d36d0ef8d61ba10fb0fe90da7634d7e13"));

    private static readonly ChannelId s_channelId = ChannelId.Zero;
    private static readonly byte[] s_nodePrivateKey =
        Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly Key s_peerNodeKey = new();
    private static readonly byte[] s_seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private const ulong CurrentCapacitySat = 10_000_000;
    private const ulong SpliceCapacitySat = 12_000_000;

    #region SP1-C-T1 funding keys

    [Fact]
    public void Given_AppendixCKeys_When_NodeBKeyChecked_Then_ItIsTheVectorsRemoteFundingKey()
    {
        // Assert: the fixture's peer key is the spec's node B funding key
        Assert.Equal(Bolt3AppendixCVectors.NodeBFundingPubkey, s_nodeBFundingKey.PubKey);
    }

    [Fact]
    public void Given_FundingKeyIndexes_When_Derived_Then_IndexZeroIsTheChannelKeyAndOthersAreDeterministic()
    {
        // Arrange: two signers over the same key file, one with the channel registered and one without (a restore
        // from a static channel backup knows only the channel key index)
        var signer = CreateSigner();
        var restored = new SplicingSigner();

        // Act
        var key0 = signer.GetFundingPubKey(s_channelId, 0);
        var key1 = signer.GetFundingPubKey(s_channelId, 1);
        var key2 = signer.GetFundingPubKey(s_channelId, 2);
        var restoredKey1 = restored.GetFundingPubKey(0, 1);

        // Assert
        Assert.Equal(Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(), (byte[])key0);
        Assert.NotEqual(key0, key1);
        Assert.NotEqual(key1, key2);
        Assert.Equal(key1, restoredKey1);
        Assert.Equal(ExpectedRotatedKey(1).ToBytes(), (byte[])key1);
    }

    [Fact]
    public void Given_UnregisteredChannel_When_GettingItsFundingKey_Then_Refused()
    {
        // Arrange
        var signer = new SplicingSigner();

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.GetFundingPubKey(s_channelId, 1));
    }

    [Fact]
    public void Given_PendingFunding_When_RegisteredTwice_Then_SecondIsANoOp()
    {
        // Arrange
        var signer = CreateSigner();
        var (splice, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);

        // Act
        var exception = Record.Exception(() => signer.RegisterFunding(s_channelId, funding));

        // Assert
        Assert.Null(exception);
        Assert.NotNull(splice);
    }

    [Fact]
    public void Given_RegisteredFunding_When_RegisteredWithOtherData_Then_Refused()
    {
        // Arrange
        var signer = CreateSigner();
        var (_, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.RegisterFunding(s_channelId,
                                                                    funding with { CapacitySatoshis = 1 }));
    }

    [Fact]
    public void Given_FundingWithAnotherLocalKey_When_Registered_Then_Refused()
    {
        // Arrange: key index 2 does not derive the key the funding names
        var signer = CreateSigner();
        var (_, funding) = BuildSplice(signer);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.RegisterFunding(s_channelId,
                                                                    funding with { LocalFundingKeyIndex = 2 }));
    }

    [Theory]
    [InlineData(ChannelFundingStatus.Current)]
    [InlineData(ChannelFundingStatus.Replaced)]
    [InlineData(ChannelFundingStatus.Discarded)]
    public void Given_FundingNotPending_When_Registered_Then_Refused(ChannelFundingStatus status)
    {
        // Arrange
        var signer = CreateSigner();
        var (_, funding) = BuildSplice(signer);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.RegisterFunding(s_channelId, funding with { Status = status }));
    }

    [Fact]
    public void Given_PendingFunding_When_SigningItsCommitment_Then_SignedWithTheRotatedKeyOverItsOutput()
    {
        // Arrange
        var signer = CreateSigner();
        var (_, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);
        var commitment = CommitmentSpending(funding.FundingTxId, funding.OutputIndex);

        // Act
        var signature = signer.SignChannelTransaction(s_channelId, funding.FundingTxId, Wrap(commitment));

        // Assert: valid for the new 2-of-2 with our rotated key, low-S
        var sigHash = FundingSigHash(commitment, 0, FundingScript(funding), funding.CapacitySatoshis);
        Assert.True(ExpectedRotatedKey(1).Verify(sigHash, ToEcdsa(signature)));
        Assert.True(ToEcdsa(signature).IsLowS);
    }

    [Fact]
    public void Given_PendingFunding_When_SigningATransactionSpendingAnotherOutput_Then_Refused()
    {
        // Arrange: the transaction spends the current funding, not the splice's
        var signer = CreateSigner();
        var (_, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(
                                           s_channelId, funding.FundingTxId, Wrap(CommitmentSpending(CurrentTxId(), 0))));
    }

    [Fact]
    public void Given_PendingFunding_When_ValidatingThePeersCommitmentSignature_Then_CheckedAgainstItsKey()
    {
        // Arrange
        var signer = CreateSigner();
        var peerKey = new Key();
        var (_, funding) = BuildSplice(signer, peerKey);
        signer.RegisterFunding(s_channelId, funding);
        var commitment = CommitmentSpending(funding.FundingTxId, funding.OutputIndex);
        var sigHash = FundingSigHash(commitment, 0, FundingScript(funding), funding.CapacitySatoshis);
        var good = ToCompact(peerKey.Sign(sigHash, new SigningOptions(SigHash.All, false)).Signature);
        var wrongKey = ToCompact(s_nodeBFundingKey.Sign(sigHash, new SigningOptions(SigHash.All, false)).Signature);

        // Act / Assert
        signer.ValidateSignature(s_channelId, funding.FundingTxId, good, Wrap(commitment));
        Assert.Throws<SignerException>(() => signer.ValidateSignature(s_channelId, funding.FundingTxId, wrongKey,
                                                                      Wrap(commitment)));
    }

    [Fact]
    public void Given_PendingSplice_When_SigningInitialCommitmentForBroadcast_Then_AppendixCBytesUnchanged()
    {
        // Arrange: a pending splice changes nothing for the initial funding (BOLT 3 Appendix C byte for byte, through
        // both the single-funding and the per-funding member)
        var signer = CreateSigner();
        var (_, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);
        var (unsigned, remote) = UnsignedCommitTx0();

        // Act (the commitment signature first: a broadcast signature stops new commitment signatures, S1)
        var commitmentSignature = signer.SignChannelTransaction(s_channelId, CurrentTxId(), unsigned);
        var viaChannel = signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned, remote);
        var viaFunding = signer.SignLocalCommitmentForBroadcast(s_channelId, CurrentTxId(), 0, unsigned, remote);

        // Assert
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature0.ToCompact(), commitmentSignature.Value);
        Assert.Equal(SignedCommitTx0Hex, Convert.ToHexString(viaChannel.RawTxBytes).ToLowerInvariant());
        Assert.Equal(SignedCommitTx0Hex, Convert.ToHexString(viaFunding.RawTxBytes).ToLowerInvariant());
    }

    #endregion

    #region SP1-C-T2 shared input (SP-I1)

    [Fact]
    public void Given_NoPersistedSpliceCommitment_When_SigningTheSharedInput_Then_Refused()
    {
        // Arrange: the new funding is registered, its commitment not persisted yet (SP-I1)
        var signer = CreateSigner();
        var (splice, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);

        // Act
        var exception = Assert.Throws<SignerException>(() => signer.SignSpliceSharedInput(
                                                           s_channelId, funding.FundingTxId, Wrap(splice), 0));

        // Assert
        Assert.Contains("SP-I1", exception.Message);
    }

    [Fact]
    public void Given_UnregisteredSplice_When_SigningTheSharedInput_Then_Refused()
    {
        // Arrange
        var signer = CreateSigner();
        var (splice, funding) = BuildSplice(signer);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSpliceSharedInput(s_channelId, funding.FundingTxId,
                                                                          Wrap(splice), 0));
        Assert.Throws<SignerException>(() => signer.MarkSpliceCommitmentPersisted(s_channelId, funding.FundingTxId,
                                                                                  0));
    }

    [Fact]
    public void Given_PersistedSpliceCommitment_When_SigningTheSharedInput_Then_LowSAllSignatureCompletesTheWitness()
    {
        // Arrange
        var signer = CreateSigner();
        var (splice, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);
        signer.MarkSpliceCommitmentPersisted(s_channelId, funding.FundingTxId, 0);

        // Act
        var ours = signer.SignSpliceSharedInput(s_channelId, funding.FundingTxId, Wrap(splice), 0);

        // Assert: low-S, SIGHASH_ALL over the current 2-of-2, and with node B's signature in the script's key order
        // the shared input executes
        var currentScript = CurrentFundingScript();
        var sigHash = FundingSigHash(splice, 0, currentScript, CurrentCapacitySat);
        var ourEcdsa = ToEcdsa(ours);
        Assert.True(ourEcdsa.IsLowS);
        Assert.True(Bolt3AppendixCVectors.NodeAFundingPubkey.Verify(sigHash, ourEcdsa));

        var theirs = s_nodeBFundingKey.Sign(sigHash, new SigningOptions(SigHash.All, false)).Signature;
        var localFirst = currentScript.ToOps().ElementAt(1).PushData.AsSpan()
                                      .SequenceEqual(Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes());
        var ourSig = new TransactionSignature(ourEcdsa, SigHash.All).ToBytes();
        var theirSig = new TransactionSignature(theirs, SigHash.All).ToBytes();
        splice.Inputs[0].WitScript = new WitScript(new[]
        {
            Array.Empty<byte>(), localFirst ? ourSig : theirSig, localFirst ? theirSig : ourSig, currentScript.ToBytes()
        });
        var spent = new TxOut(Money.Satoshis(CurrentCapacitySat), currentScript.WitHash.ScriptPubKey);
        Assert.True(splice.Inputs.AsIndexedInputs().First().VerifyScript(spent, out var error), error.ToString());
    }

    [Fact]
    public void Given_SpliceCommitmentPersistedAtAnOlderNumber_When_SigningTheSharedInput_Then_Refused()
    {
        // Arrange: the local commitment moved on after the splice commitment was persisted
        var signer = CreateSigner(withChannelKey: true);
        var (splice, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);
        signer.MarkSpliceCommitmentPersisted(s_channelId, funding.FundingTxId, 0);
        signer.AdvanceLocalCommitment(s_channelId, 1);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSpliceSharedInput(s_channelId, funding.FundingTxId,
                                                                          Wrap(splice), 0));
    }

    [Fact]
    public void Given_ATransactionThatIsNotTheSplice_When_SigningTheSharedInput_Then_Refused()
    {
        // Arrange: another transaction (other amount) than the registered splice, and the shared input at a wrong index
        var signer = CreateSigner();
        var (splice, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);
        signer.MarkSpliceCommitmentPersisted(s_channelId, funding.FundingTxId, 0);
        var other = splice.Clone();
        other.Outputs[0].Value = Money.Satoshis(SpliceCapacitySat - 1);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSpliceSharedInput(s_channelId, funding.FundingTxId,
                                                                          Wrap(other), 0));
        Assert.Throws<SignerException>(() => signer.SignSpliceSharedInput(s_channelId, funding.FundingTxId,
                                                                          Wrap(splice), 1));
    }

    [Fact]
    public void Given_ARegisteredSpliceWithAnotherOutput_When_SigningTheSharedInput_Then_Refused()
    {
        // Arrange: the funding names a capacity the transaction's output does not have
        var signer = CreateSigner();
        var (splice, funding) = BuildSplice(signer);
        var other = funding with { CapacitySatoshis = SpliceCapacitySat + 1 };
        signer.RegisterFunding(s_channelId, other);
        signer.MarkSpliceCommitmentPersisted(s_channelId, other.FundingTxId, 0);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSpliceSharedInput(s_channelId, other.FundingTxId,
                                                                          Wrap(splice), 0));
    }

    [Fact]
    public void Given_DataLossOrABroadcast_When_SigningTheSharedInput_Then_Refused()
    {
        // Arrange
        var lost = CreateSigner();
        var broadcast = CreateSigner();
        foreach (var signer in new[] { lost, broadcast })
        {
            var (_, f) = BuildSplice(signer);
            signer.RegisterFunding(s_channelId, f);
            signer.MarkSpliceCommitmentPersisted(s_channelId, f.FundingTxId, 0);
        }

        lost.MarkDataLoss(s_channelId);
        broadcast.MarkBroadcastSigned(s_channelId, 0);
        var (splice, funding) = BuildSplice(lost);

        // Act / Assert
        Assert.Throws<SignerException>(() => lost.SignSpliceSharedInput(s_channelId, funding.FundingTxId,
                                                                        Wrap(splice), 0));
        Assert.Throws<SignerException>(() => broadcast.SignSpliceSharedInput(s_channelId, funding.FundingTxId,
                                                                             Wrap(splice), 0));
    }

    [Fact]
    public void Given_PersistedMarkInTheSigningInfo_When_RegisteredAfterARestart_Then_SharedInputSigned()
    {
        // Arrange: the channel reloaded with its pending splice and the persisted commitment of it (SP-I1 restored)
        var reference = CreateSigner();
        var (splice, funding) = BuildSplice(reference);
        var restarted = new SplicingSigner();
        restarted.RegisterChannel(s_channelId, SigningInfo() with
        {
            Fundings = [funding],
            PersistedSpliceCommitments = new Dictionary<TxId, ulong> { [funding.FundingTxId] = 0 }
        });
        var withoutMark = new SplicingSigner();
        withoutMark.RegisterChannel(s_channelId, SigningInfo() with { Fundings = [funding] });

        // Act
        var signature = restarted.SignSpliceSharedInput(s_channelId, funding.FundingTxId, Wrap(splice), 0);

        // Assert
        Assert.True(ToEcdsa(signature).IsLowS);
        Assert.Throws<SignerException>(() => withoutMark.SignSpliceSharedInput(s_channelId, funding.FundingTxId,
                                                                               Wrap(splice), 0));
    }

    [Fact]
    public void Given_ThePeersSharedInputSignature_When_Validated_Then_OnlyAValidLowSOneIsAccepted()
    {
        // Arrange
        var signer = CreateSigner();
        var (splice, _) = BuildSplice(signer);
        var sigHash = FundingSigHash(splice, 0, CurrentFundingScript(), CurrentCapacitySat);
        var good = s_nodeBFundingKey.Sign(sigHash, new SigningOptions(SigHash.All, false)).Signature.MakeCanonical();
        var highS = good.ToCompact();
        new NBitcoin.Secp256k1.Scalar(highS.AsSpan(32, 32)).Negate().WriteToSpan(highS.AsSpan(32));
        var ours = Bolt3AppendixCVectors.NodeAFundingPrivkey.Sign(sigHash, new SigningOptions(SigHash.All, false))
                                        .Signature;

        // Act / Assert
        signer.ValidateSpliceSharedInputSignature(s_channelId, Wrap(splice), 0, ToCompact(good));
        Assert.Throws<SignerException>(() => signer.ValidateSpliceSharedInputSignature(s_channelId, Wrap(splice), 0,
                                                                                      ToCompact(ours)));
        Assert.Throws<SignerException>(() => signer.ValidateSpliceSharedInputSignature(s_channelId, Wrap(splice), 0,
                                                                                      highS));
        Assert.Throws<SignerException>(() => signer.ValidateSpliceSharedInputSignature(s_channelId, Wrap(splice), 1,
                                                                                      ToCompact(good)));
    }

    #endregion

    #region SP1-C-T3 per-funding S1 (SP-I4)

    [Fact]
    public void Given_BroadcastOfNOnTheCurrentFunding_When_SigningNOnThePendingFunding_Then_Allowed()
    {
        // Arrange: commitment 0 of the current funding is signed for broadcast; the splice may confirm instead
        var signer = CreateSigner(withChannelKey: true);
        var peerKey = new Key();
        var (_, funding) = BuildSplice(signer, peerKey);
        signer.RegisterFunding(s_channelId, funding);
        var (unsigned, remote) = UnsignedCommitTx0();
        _ = signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned, remote);
        var spliceCommitment = CommitmentSpending(funding.FundingTxId, funding.OutputIndex);
        var peerSignature = PeerSignature(peerKey, spliceCommitment, funding);

        // Act
        var signed = signer.SignLocalCommitmentForBroadcast(s_channelId, funding.FundingTxId, 0,
                                                            Wrap(spliceCommitment), peerSignature);

        // Assert: a complete 2-of-2 spend of the splice's output
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        var spent = new TxOut(Money.Satoshis(SpliceCapacitySat), FundingScript(funding).WitHash.ScriptPubKey);
        Assert.True(tx.Inputs.AsIndexedInputs().First().VerifyScript(spent, out var error), error.ToString());
    }

    [Fact]
    public void Given_BroadcastOfN_When_SigningAnotherNumberOnAnyFunding_Then_Refused()
    {
        // Arrange
        var signer = CreateSigner(withChannelKey: true);
        var peerKey = new Key();
        var (_, funding) = BuildSplice(signer, peerKey);
        signer.RegisterFunding(s_channelId, funding);
        signer.AdvanceLocalCommitment(s_channelId, 3);
        var spliceCommitment = CommitmentSpending(funding.FundingTxId, funding.OutputIndex);
        var peerSignature = PeerSignature(peerKey, spliceCommitment, funding);
        _ = signer.SignLocalCommitmentForBroadcast(s_channelId, funding.FundingTxId, 3, Wrap(spliceCommitment),
                                                   peerSignature);

        // Act / Assert (SP-I4): n + 1 anywhere, no advance, no reveal of n, no new commitment signatures
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                           s_channelId, funding.FundingTxId, 4, Wrap(spliceCommitment),
                                           peerSignature));
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                           s_channelId, CurrentTxId(), 4, UnsignedCommitTx0().Unsigned,
                                           UnsignedCommitTx0().Remote));
        Assert.Throws<SignerException>(() => signer.AdvanceLocalCommitment(s_channelId, 4));
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(s_channelId, 3));
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(s_channelId, funding.FundingTxId,
                                                                           Wrap(spliceCommitment)));
        Assert.Equal(32, ((byte[])signer.RevealPerCommitmentSecret(s_channelId, 2)).Length);
    }

    [Fact]
    public void Given_BroadcastMarkInTheSigningInfo_When_RegisteredWithItsSplice_Then_SP_I4Restored()
    {
        // Arrange: a restart after commitment 2 was signed for broadcast; the splice is pending
        var reference = CreateSigner();
        var peerKey = new Key();
        var (_, funding) = BuildSplice(reference, peerKey);
        var signer = new SplicingSigner(withChannelKey: true);
        signer.RegisterChannel(s_channelId, SigningInfo(localCommitmentNumber: 2) with
        {
            BroadcastSignedCommitmentNumber = 2,
            Fundings = [funding]
        });
        var spliceCommitment = CommitmentSpending(funding.FundingTxId, funding.OutputIndex);
        var peerSignature = PeerSignature(peerKey, spliceCommitment, funding);

        // Act
        var same = Record.Exception(() => signer.SignLocalCommitmentForBroadcast(
                                        s_channelId, funding.FundingTxId, 2, Wrap(spliceCommitment), peerSignature));

        // Assert
        Assert.Null(same);
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                           s_channelId, funding.FundingTxId, 3, Wrap(spliceCommitment),
                                           peerSignature));
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(s_channelId, 2));
        Assert.Throws<SignerException>(() => signer.AdvanceLocalCommitment(s_channelId, 3));
    }

    [Fact]
    public void Given_ARevokedNumber_When_SigningItForBroadcastOnAPendingFunding_Then_Refused()
    {
        // Arrange (I4 per funding)
        var signer = CreateSigner(withChannelKey: true);
        var peerKey = new Key();
        var (_, funding) = BuildSplice(signer, peerKey);
        signer.RegisterFunding(s_channelId, funding);
        signer.AdvanceLocalCommitment(s_channelId, 5);
        var spliceCommitment = CommitmentSpending(funding.FundingTxId, funding.OutputIndex);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                           s_channelId, funding.FundingTxId, 4, Wrap(spliceCommitment),
                                           PeerSignature(peerKey, spliceCommitment, funding)));
    }

    #endregion

    #region Lock

    [Fact]
    public void Given_LockedSplice_When_Signing_Then_ItIsTheCurrentFundingAndTheOthersAreRetired()
    {
        // Arrange: two RBF siblings; the second is locked
        var signer = CreateSigner(withChannelKey: true);
        var peerKey = new Key();
        var (_, first) = BuildSplice(signer, peerKey);
        var (_, second) = BuildSplice(signer, peerKey, capacity: SpliceCapacitySat + 1_000, keyIndex: 2);
        signer.RegisterFunding(s_channelId, first);
        signer.RegisterFunding(s_channelId, second);

        // Act
        signer.LockFunding(s_channelId, second.FundingTxId);
        signer.LockFunding(s_channelId, second.FundingTxId);

        // Assert: the single-funding members now sign for the locked splice with its rotated key; the replaced and
        // the discarded fundings are no longer signed for, but their signatures can still be checked (SP-I5)
        var commitment = CommitmentSpending(second.FundingTxId, second.OutputIndex);
        var signature = signer.SignChannelTransaction(s_channelId, Wrap(commitment));
        var sigHash = FundingSigHash(commitment, 0, FundingScript(second), second.CapacitySatoshis);
        Assert.True(ExpectedRotatedKey(2).Verify(sigHash, ToEcdsa(signature)));
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(
                                           s_channelId, first.FundingTxId,
                                           Wrap(CommitmentSpending(first.FundingTxId, first.OutputIndex))));
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(
                                           s_channelId, CurrentTxId(), UnsignedCommitTx0().Unsigned));
        signer.ValidateSignature(s_channelId, CurrentTxId(), UnsignedCommitTx0().Remote, UnsignedCommitTx0().Unsigned);
        Assert.Throws<SignerException>(() => signer.LockFunding(s_channelId, first.FundingTxId));
    }

    [Fact]
    public void Given_ASpliceLockedWithItsShortChannelId_When_SigningItsAnnouncement_Then_ItIsSignedWithTheRotatedKey()
    {
        // Arrange (regression: the pending funding was registered before it confirmed, so the lock left the channel
        // without a short channel id and its announcement was refused)
        var signer = new SplicingSigner();
        signer.RegisterChannel(s_channelId, SigningInfo() with { AnnounceChannel = true });
        var (_, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);
        var scid = new ShortChannelId(900_000, 4, funding.OutputIndex);
        var announcement = UnsignedAnnouncement(funding, scid);

        // Act
        signer.LockFunding(s_channelId, funding.FundingTxId, scid);
        var signatures = signer.SignChannelAnnouncement(s_channelId, announcement, scid);

        // Assert
        var hash = new uint256(SHA256.HashData(SHA256.HashData(announcement)));
        Assert.True(ExpectedRotatedKey(1).Verify(hash, ToEcdsa(signatures.BitcoinSignature)));
    }

    [Fact]
    public void Given_APendingSpliceRegisteredAgainOnceConfirmed_When_Locked_Then_ItsAnnouncementIsSigned()
    {
        // Arrange
        var signer = new SplicingSigner();
        signer.RegisterChannel(s_channelId, SigningInfo() with { AnnounceChannel = true });
        var (_, funding) = BuildSplice(signer);
        var scid = new ShortChannelId(900_001, 2, funding.OutputIndex);
        signer.RegisterFunding(s_channelId, funding);
        Assert.Throws<SignerException>(() => signer.RegisterFunding(
                                           s_channelId, funding with { LocalFundingKeyIndex = 2 }));

        // Act
        signer.RegisterFunding(s_channelId, funding with { ShortChannelId = scid, ConfirmedHeight = 900_001 });
        signer.LockFunding(s_channelId, funding.FundingTxId);
        var announcement = UnsignedAnnouncement(funding, scid);
        var signatures = signer.SignChannelAnnouncement(s_channelId, announcement, scid);

        // Assert
        var hash = new uint256(SHA256.HashData(SHA256.HashData(announcement)));
        Assert.True(ExpectedRotatedKey(1).Verify(hash, ToEcdsa(signatures.BitcoinSignature)));
    }

    [Fact]
    public void Given_LockedSplice_When_RegisteredAgainFromTheChannelModel_Then_TheLockedFundingStays()
    {
        // Arrange: the model still names the original funding keys but the locked txid (predating the lock's keys)
        var signer = CreateSigner(withChannelKey: true);
        var (_, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);
        signer.LockFunding(s_channelId, funding.FundingTxId);
        var stale = SigningInfo() with
        {
            FundingTxId = funding.FundingTxId,
            FundingOutputIndex = funding.OutputIndex,
            RemoteNodeId = Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes()
        };

        // Act
        var exception = Record.Exception(() => signer.RegisterChannel(s_channelId, stale));
        var original = Record.Exception(() => signer.RegisterChannel(s_channelId, SigningInfo()));

        // Assert
        Assert.Null(exception);
        Assert.Null(original);
        var commitment = CommitmentSpending(funding.FundingTxId, funding.OutputIndex);
        var sigHash = FundingSigHash(commitment, 0, FundingScript(funding), funding.CapacitySatoshis);
        Assert.True(ExpectedRotatedKey(1).Verify(sigHash,
                                                 ToEcdsa(signer.SignChannelTransaction(s_channelId, Wrap(commitment)))));
    }

    [Fact]
    public void Given_AnotherChannelsData_When_RegisteredOverASplicedChannel_Then_StillRefused()
    {
        // Arrange
        var signer = CreateSigner(withChannelKey: true);
        var (_, funding) = BuildSplice(signer);
        signer.RegisterFunding(s_channelId, funding);

        // Act / Assert: an unknown outpoint is never accepted
        Assert.Throws<SignerException>(() => signer.RegisterChannel(s_channelId, SigningInfo() with
        {
            FundingOutputIndex = 7
        }));
    }

    #endregion

    #region Helpers

    private static SplicingSigner CreateSigner(bool withChannelKey = true)
    {
        var signer = new SplicingSigner(withChannelKey);
        signer.RegisterChannel(s_channelId, SigningInfo());
        return signer;
    }

    private static ChannelSigningInfo SigningInfo(ulong localCommitmentNumber = 0) =>
        new(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(), 0, Bolt3AppendixBVectors.FundingSatoshis,
            Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(), Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(), 0,
            localCommitmentNumber: localCommitmentNumber);

    private static TxId CurrentTxId() => Bolt3AppendixBVectors.ExpectedTxId.ToBytes();

    private static PubKey ExpectedRotatedKey(int index) =>
        ExtKey.CreateFromSeed(s_seed).Derive(0, true).Derive(index, true).PrivateKey.PubKey;

    private static Script CurrentFundingScript() =>
        PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, Bolt3AppendixCVectors.NodeAFundingPubkey,
                                                            Bolt3AppendixCVectors.NodeBFundingPubkey);

    private static Script FundingScript(ChannelFunding funding) =>
        new FundingOutputBuilder().Build(new Domain.Bitcoin.Transactions.Outputs.FundingOutputInfo(
                                             Domain.Money.LightningMoney.Satoshis(funding.CapacitySatoshis),
                                             funding.LocalFundingPubKey, funding.RemoteFundingPubKey,
                                             funding.FundingTxId, funding.OutputIndex))
                                  .RedeemScript;

    /// <summary>
    /// A splice transaction: input 0 the current funding output, input 1 a wallet input, output 0 the new funding
    /// (our key <paramref name="keyIndex"/>, the peer's <paramref name="peerKey"/>), and its pending funding.
    /// </summary>
    private static (Transaction Splice, ChannelFunding Funding) BuildSplice(SplicingSigner signer, Key? peerKey = null,
                                                                          ulong capacity = SpliceCapacitySat,
                                                                          uint keyIndex = 1)
    {
        var localKey = signer.GetFundingPubKey(s_channelId, keyIndex);
        var remoteKey = (peerKey ?? s_nodeBFundingKey).PubKey;
        var fundingScript = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(
            2, new[] { new PubKey(localKey), remoteKey }.OrderBy(k => k.ToHex(), StringComparer.Ordinal).ToArray());

        var tx = Network.Main.CreateTransaction();
        tx.Version = 2;
        tx.Inputs.Add(new OutPoint(Bolt3AppendixBVectors.ExpectedTxId, 0));
        tx.Inputs.Add(new OutPoint(uint256.One, 3));
        tx.Outputs.Add(new TxOut(Money.Satoshis(capacity), fundingScript.WitHash.ScriptPubKey));

        var funding = new ChannelFunding(tx.GetHash().ToBytes(), 0, capacity, localKey, remoteKey.ToBytes(),
                                         keyIndex, 2_000_000_000, 0, ChannelFundingKind.Splice,
                                         ChannelFundingStatus.Pending, 253, 0);
        return (tx, funding);
    }

    private static Transaction CommitmentSpending(TxId fundingTxId, ushort index)
    {
        var tx = Network.Main.CreateTransaction();
        tx.Version = 2;
        tx.Inputs.Add(new OutPoint(new uint256((byte[])fundingTxId), index));
        tx.Outputs.Add(new TxOut(Money.Satoshis(9_000_000), new Key().PubKey.WitHash.ScriptPubKey));
        return tx;
    }

    private static CompactSignature PeerSignature(Key peerKey, Transaction commitment, ChannelFunding funding) =>
        ToCompact(peerKey.Sign(FundingSigHash(commitment, 0, FundingScript(funding), funding.CapacitySatoshis),
                               new SigningOptions(SigHash.All, false)).Signature);

    private static uint256 FundingSigHash(Transaction tx, int input, Script script, ulong amountSat) =>
        tx.GetSignatureHash(script, input, SigHash.All, new TxOut(Money.Satoshis(amountSat), script.WitHash.ScriptPubKey),
                            HashVersion.WitnessV0);

    private static SignedTransaction Wrap(Transaction tx) => new(tx.GetHash().ToBytes(), tx.ToBytes());

    private static ECDSASignature ToEcdsa(CompactSignature signature)
    {
        Assert.True(ECDSASignature.TryParseFromCompact(signature, out var ecdsa));
        return ecdsa;
    }

    private static CompactSignature ToCompact(ECDSASignature signature) => signature.MakeCanonical().ToCompact();

    private static (SignedTransaction Unsigned, CompactSignature Remote) UnsignedCommitTx0()
    {
        var tx = Transaction.Parse(SignedCommitTx0Hex, Network.Main);
        tx.Inputs[0].WitScript = WitScript.Empty;
        return (new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()),
                Bolt3AppendixCVectors.NodeBSignature0.ToCompact());
    }

    /// <summary>
    /// The unsigned <c>channel_announcement</c> of <paramref name="funding"/> at <paramref name="scid"/> on the signer's
    /// default chain (mainnet), between our node (<see cref="s_nodePrivateKey"/>) and a peer.
    /// </summary>
    private static byte[] UnsignedAnnouncement(ChannelFunding funding, ShortChannelId scid)
    {
        using var nodeKey = new Key(s_nodePrivateKey);
        var us = nodeKey.PubKey.ToBytes();
        var peer = s_peerNodeKey.PubKey.ToBytes();
        var weAreNode1 = ((ReadOnlySpan<byte>)us).SequenceCompareTo(peer) < 0;
        byte[] ourBitcoinKey = funding.LocalFundingPubKey;
        byte[] peerBitcoinKey = funding.RemoteFundingPubKey;
        return
        [
            0x00, 0x00,
            .. (byte[])new NodeOptions().BitcoinNetwork.ChainHash,
            .. (byte[])scid,
            .. weAreNode1 ? us : peer,
            .. weAreNode1 ? peer : us,
            .. weAreNode1 ? ourBitcoinKey : peerBitcoinKey,
            .. weAreNode1 ? peerBitcoinKey : ourBitcoinKey
        ];
    }

    private static ISecureKeyManager KeyManager(bool withChannelKey)
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodeKeyPair())
                  .Returns(() =>
                  {
                      using var nodeKey = new Key(s_nodePrivateKey);
                      return new CryptoKeyPair(s_nodePrivateKey.ToArray(), nodeKey.PubKey.ToBytes());
                  });
        if (withChannelKey)
            keyManager.Setup(k => k.GetChannelKeyAtIndex(0)).Returns(() => ExtKey.CreateFromSeed(s_seed).ToBytes());
        return keyManager.Object;
    }

    /// <summary>
    /// A <see cref="LocalLightningSigner"/> whose original funding key (index 0) is Appendix C's node A funding key;
    /// rotated keys come from the real derivation over a fixed seed.
    /// </summary>
    private sealed class SplicingSigner(bool withChannelKey = true)
        : LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
                               NullLogger<LocalLightningSigner>.Instance, new NodeOptions(), KeyManager(withChannelKey),
                               new Mock<IUtxoMemoryRepository>().Object)
    {
        protected override Key GenerateFundingPrivateKey(uint channelKeyIndex) =>
            new(Bolt3AppendixCVectors.NodeAFundingPrivkey.ToBytes());
    }

    #endregion
}