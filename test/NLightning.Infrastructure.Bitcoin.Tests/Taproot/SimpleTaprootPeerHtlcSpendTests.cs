using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Outputs;
using Bitcoin.Signers;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Models;
using Domain.Onchain.Parsers;
using Domain.Onchain.Taproot;
using static SimpleTaprootOnchainSweepTests;

/// <summary>
/// NL-966 (T4): the spends of the HTLC outputs of the peer's simple taproot commitments, built by the production mapper,
/// <see cref="SweepInputFactory"/>, <see cref="SweepTransactionBuilder"/> and <see cref="LocalLightningSigner"/> and
/// executed by NBitcoin's interpreter against every output they spend: our timeout claim (accepted output's timeout
/// leaf, <c>nLockTime = cltv_expiry</c>, <c>nSequence</c> 1), our preimage claim (offered output's success leaf), the
/// key-path penalties of a revoked commitment's HTLC outputs and of the peer's second-level output (internal key = the
/// revocation key, tweaked with the tree's merkle root), and the witness parser on what they and the spec vectors put
/// on chain.
/// </summary>
public class SimpleTaprootPeerHtlcSpendTests
{
    [Fact]
    public void Given_OurOfferedHtlcOnThePeersTaprootCommitment_When_Claimed_Then_TheTimeoutLeafSpendsItAfterCltv()
    {
        // Arrange: Bob's commitment on chain, Alice's offered HTLC 0 (cltv 500) is an accepted output there
        var (kit, _, commitment, map) = PeerCommitment();
        var output = Assert.Single(map.Outputs, o => o is
        {
            Kind: OutputDescriptorKind.RemoteReceivedHtlc, Htlc.Id: 0
        });
        var input = SweepInputFactory.HtlcTimeoutClaim(output, commitment.TxId, map.PerCommitmentPoint);

        // Act
        var tx = Load(Sweep(kit.Alice, input));

        // Assert: <sig> <timeout leaf> <control block>, nLockTime = cltv_expiry, nSequence 1, valid by execution
        Assert.Equal(65, output.TaprootControlBlock!.Length);
        Assert.Equal(500u, (uint)tx.LockTime);
        Assert.Equal(1u, (uint)tx.Inputs[0].Sequence);
        Assert.Equal(3, tx.Inputs[0].WitScript.PushCount);
        Assert.Equal(64, tx.Inputs[0].WitScript[0].Length);
        AssertSpends(tx, commitment, output.Vout);
        Assert.Equal(HtlcSpendPath.TimeoutClaim, HtlcWitnessParser.Parse(Witness(tx)).Path);

        // The leaf's CLTV and CSV really hold: an earlier lock time or a zero sequence is refused by the interpreter
        var early = tx.Clone();
        early.LockTime = new LockTime(499);
        Assert.NotNull(early.CreateValidator([Output(commitment, output.Vout)]).ValidateInput(0).Error);
        var noCsv = tx.Clone();
        noCsv.Inputs[0].Sequence = new Sequence(0);
        Assert.NotNull(noCsv.CreateValidator([Output(commitment, output.Vout)]).ValidateInput(0).Error);
    }

    [Fact]
    public void Given_ThePeersOfferedHtlcOnItsTaprootCommitment_When_ClaimedWithThePreimage_Then_TheSuccessLeafSpendsIt()
    {
        // Arrange: Bob's offered HTLC 1 (cltv 503, preimage index 3) is an offered output of Bob's commitment
        var (kit, _, commitment, map) = PeerCommitment();
        var output = Assert.Single(map.Outputs, o => o is { Kind: OutputDescriptorKind.RemoteOfferedHtlc, Htlc.Id: 1 });
        var preimage = s_preimages[3];
        var input = SweepInputFactory.HtlcPreimageClaim(output, commitment.TxId, map.PerCommitmentPoint, preimage);

        // Act
        var tx = Load(Sweep(kit.Alice, input));

        // Assert: <sig> <preimage> <success leaf> <control block>, nSequence 1, nLockTime below cltv_expiry
        Assert.Equal(4, tx.Inputs[0].WitScript.PushCount);
        Assert.Equal(preimage, tx.Inputs[0].WitScript[1]);
        Assert.Equal(output.WitnessScript, tx.Inputs[0].WitScript[2]);
        Assert.Equal(1u, (uint)tx.Inputs[0].Sequence);
        Assert.True((uint)tx.LockTime < 503u);
        AssertSpends(tx, commitment, output.Vout);

        // The parser reads the preimage back (what the peer sees when we claim, and what we read from the peer)
        Assert.True(HtlcWitnessParser.TryExtractPreimage(Witness(tx), output.Htlc!.Value.PaymentHash, out var read));
        Assert.Equal(preimage, (byte[])read);

        // A wrong preimage fails the leaf's hash check
        var wrong = Load(Sweep(kit.Alice, input with { Preimage = s_preimages[2] }));
        Assert.NotNull(wrong.CreateValidator([Output(commitment, output.Vout)]).ValidateInput(0).Error);
    }

    [Fact]
    public void Given_ARevokedTaprootCommitment_When_Penalized_Then_EveryHtlcOutputIsTakenByKeyPathInOneBatch()
    {
        // Arrange: Bob revoked commitment `Number` and broadcast it; Alice holds his secret
        var kit = new TaprootSignerKit(bobLocalNumber: Number + 1);
        var channel = CreateChannel(kit, alice: true);
        var secret = kit.Bob.RevealPerCommitmentSecret(TaprootSignerKit.ChannelId, Number);
        var point = PointOf(secret);
        var commitment = Build(kit.Alice, channel, CommitmentSide.Remote, point);
        var map = Mapper(kit.Alice).Map(channel, Spec(alice: true), CommitmentCase.Revoked, Number, point, commitment);
        var revocationPubKey = s_keyDerivation.DeriveRevocationPubKey(channel.LocalKeySet.RevocationCompactBasepoint,
                                                                      point);
        var outputs = map.Outputs.Where(o => o.Kind is OutputDescriptorKind.RevokedHtlc
                                                 or OutputDescriptorKind.RevokedToLocal)
                         .ToList();

        // Act: one batch with to_local (revocation leaf) and the four HTLC outputs (key path)
        var inputs = outputs.Select(o => SweepInputFactory.Penalty(o, commitment.TxId, secret, revocationPubKey))
                            .ToList();
        var builder = new SweepTransactionBuilder(s_options);
        var tx = Load(builder.Sign(builder.Build(inputs, s_destination, 2_500), kit.Alice,
                                   TaprootSignerKit.ChannelId));

        // Assert: four 1-item witnesses, all inputs valid by execution against every spent output (BIP 341)
        Assert.Equal(4, inputs.Count(i => i.IsTaprootKeyPath));
        var spent = tx.Inputs.Select(i => Output(commitment, i.PrevOut.N)).ToArray();
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var error = tx.CreateValidator(spent).ValidateInput(i).Error;
            Assert.True(error is null, $"input {i}: {error}");
            if (inputs[i].IsTaprootKeyPath)
            {
                Assert.Equal(1, tx.Inputs[i].WitScript.PushCount);
                Assert.Equal(64, tx.Inputs[i].WitScript[0].Length);
                Assert.Equal(HtlcSpendPath.Revocation, HtlcWitnessParser.Parse(Witness(tx, i)).Path);
            }

            Assert.Equal(SweepFeePolicyRbfSequence, (uint)tx.Inputs[i].Sequence);
        }
    }

    [Fact]
    public void Given_ThePeersSecondLevelOutputOfARevokedTaprootCommitment_When_Penalized_Then_KeyPathSpendIsValid()
    {
        // Arrange: Bob's HTLC-timeout of revoked commitment `Number` pays its second-level output (internal key the
        // revocation key, one delay leaf with Bob's delayed key and the delay Alice imposes)
        var kit = new TaprootSignerKit(bobLocalNumber: Number + 1);
        var channel = CreateChannel(kit, alice: true);
        var secret = kit.Bob.RevealPerCommitmentSecret(TaprootSignerKit.ChannelId, Number);
        var point = PointOf(secret);
        var (parent, output) = SecondLevel(channel, point, 6_000);

        // Act
        var input = SweepInputFactory.TaprootSecondLevelPenalty(new TxId(parent.GetHash().ToBytes()), 0, 6_000,
                                                                output.ScriptPubKey.ToBytes(),
                                                                output.DelayLeaf.Script.ToBytes(),
                                                                output.GetControlBlock(output.DelayLeaf), secret);
        var tx = Load(Sweep(kit.Alice, input));

        // Assert: the merkle root is the single leaf's, the key path spends it at once (no CSV)
        Assert.Equal(output.Tree.MerkleRoot.ToBytes(), input.TaprootMerkleRoot);
        Assert.Equal(1, tx.Inputs[0].WitScript.PushCount);
        var error = tx.CreateValidator([parent.Outputs[0]]).ValidateInput(0).Error;
        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void Given_AKeyPathPenaltyWithTheWrongSecret_When_Signed_Then_TheSignerRefuses()
    {
        // Arrange: a secret of another commitment (its revocation key does not make the output key)
        var kit = new TaprootSignerKit(bobLocalNumber: Number + 2);
        var channel = CreateChannel(kit, alice: true);
        var secret = kit.Bob.RevealPerCommitmentSecret(TaprootSignerKit.ChannelId, Number);
        var other = kit.Bob.RevealPerCommitmentSecret(TaprootSignerKit.ChannelId, Number + 1);
        var (parent, output) = SecondLevel(channel, PointOf(secret), 6_000);
        var input = SweepInputFactory.TaprootSecondLevelPenalty(new TxId(parent.GetHash().ToBytes()), 0, 6_000,
                                                                output.ScriptPubKey.ToBytes(),
                                                                output.DelayLeaf.Script.ToBytes(),
                                                                output.GetControlBlock(output.DelayLeaf), other);
        var unsigned = new SweepTransactionBuilder(s_options).Build([input], s_destination, 2_500);

        // Act / Assert
        Assert.Throws<Domain.Exceptions.SignerException>(
            () => kit.Alice.SignSweepInput(TaprootSignerKit.ChannelId, unsigned.GetSigningContext(0)));
    }

    [Fact]
    public void Given_TheTaprootToLocalVectors_When_TheMerkleRootIsRecomputedFromALeafAndItsControlBlock_Then_ItMatches()
    {
        // Arrange: the spec's to_local tree (two leaves) and to_remote tree (one leaf)
        var toLocal = SimpleTaprootVectors.Scripts.GetProperty("to_local");
        var settle = SimpleTaprootVectors.Hex(toLocal.GetProperty("scripts"), "settle");
        var revocationHash = SimpleTaprootVectors.Hex(toLocal.GetProperty("leaf_hashes"), "revocation");
        var nums = SimpleTaprootVectors.Hex(toLocal, "internal_key")[1..];
        byte[] controlBlock = [0xc0, .. nums, .. revocationHash];
        var toRemote = SimpleTaprootVectors.Scripts.GetProperty("to_remote");

        // Act
        var root = TapscriptMerkleRoot.Compute(settle, controlBlock);
        var single = TapscriptMerkleRoot.Compute(SimpleTaprootVectors.Hex(toRemote.GetProperty("scripts"), "settle"),
                                                 [0xc1, .. nums]);

        // Assert (the parity bit does not change the root)
        Assert.Equal(SimpleTaprootVectors.Hex(toLocal, "tapscript_root"), root);
        Assert.Equal(SimpleTaprootVectors.Hex(toRemote, "tapscript_root"), single);
    }

    [Fact]
    public void Given_TheSpecVectorsHtlcResolutionTransactions_When_Parsed_Then_SuccessesRevealTheirPreimages()
    {
        // Arrange
        var successes = 0;
        var timeouts = 0;
        foreach (var vector in SimpleTaprootVectors.Transactions)
        {
            var hashes = vector.Htlcs.ToDictionary(h => Convert.ToHexString(h.PaymentHash), h => h);
            foreach (var desc in vector.HtlcDescs)
            {
                var tx = Transaction.Parse(desc.ResolutionTxHex, Network.Main);

                // Act
                var parsed = HtlcWitnessParser.Parse(Witness(tx));

                // Assert: an HTLC-success shows its preimage, an HTLC-timeout none
                if (parsed.Path == HtlcSpendPath.HtlcSuccessTransaction)
                {
                    var preimage = (byte[])parsed.Preimage!.Value;
                    var htlc = hashes[Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(preimage))];
                    Assert.True(htlc.Incoming);
                    Assert.True(HtlcWitnessParser.TryExtractPreimage(Witness(tx), new Hash(htlc.PaymentHash),
                                                                     out _));
                    successes++;
                }
                else
                {
                    Assert.Equal(HtlcSpendPath.HtlcTimeoutTransaction, parsed.Path);
                    Assert.Null(parsed.Preimage);
                    timeouts++;
                }
            }
        }

        Assert.True(successes > 0 && timeouts > 0, $"{successes} successes, {timeouts} timeouts");
    }

    #region Helpers

    private const uint SweepFeePolicyRbfSequence = Domain.Onchain.Fees.SweepFeePolicy.RbfSequence;

    private static (TaprootSignerKit Kit, ChannelModel Channel, ChainTx Commitment, CommitmentOutputMap Map)
        PeerCommitment()
    {
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var channel = CreateChannel(kit, alice: true);
        var bobPoint = kit.Bob.GetPerCommitmentPoint(0u, Number);
        var commitment = Build(kit.Alice, channel, CommitmentSide.Remote, bobPoint);
        var map = Mapper(kit.Alice).Map(channel, Spec(alice: true), CommitmentCase.Remote, Number, bobPoint,
                                        commitment);
        return (kit, channel, commitment, map);
    }

    /// <summary>A transaction paying the peer's second-level output of the revoked commitment at <paramref name="point"/>.</summary>
    private static (Transaction Parent, TaprootHtlcResolutionOutput Output) SecondLevel(ChannelModel channel,
        CompactPubKey point, long amountSat)
    {
        var revocation = s_keyDerivation.DeriveRevocationPubKey(channel.LocalKeySet.RevocationCompactBasepoint, point);
        var theirDelayed = s_keyDerivation.DerivePublicKey(channel.RemoteKeySet!.DelayedPaymentCompactBasepoint, point);
        var output = new TaprootHtlcResolutionOutput(LightningMoney.Satoshis(amountSat), new PubKey(theirDelayed),
                                                     new PubKey(revocation), AliceToSelfDelay);
        var parent = Transaction.Create(Network.Main);
        parent.Inputs.Add(new OutPoint(uint256.One, 0));
        parent.Outputs.Add(new TxOut(Money.Satoshis(amountSat), output.ScriptPubKey));
        return (parent, output);
    }

    private static CompactPubKey PointOf(Secret secret)
    {
        using var key = new Key((byte[])secret);
        return new CompactPubKey(key.PubKey.ToBytes());
    }

    private static Transaction Load(SignedTransaction signed) => Transaction.Load(signed.RawTxBytes, Network.Main);

    private static byte[][] Witness(Transaction tx, int input = 0) => tx.Inputs[input].WitScript.Pushes.ToArray();

    #endregion
}