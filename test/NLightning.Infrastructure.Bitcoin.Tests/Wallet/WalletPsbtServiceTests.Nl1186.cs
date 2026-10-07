using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Protocol.Constants;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// NL-1186: SignPsbt and FinalizePsbt over PSBTs that mix leased wallet inputs with other parties' inputs (only leased
/// wallet outputs are signed, the others are never touched), a collaborative publish, a P2TR change, LND's
/// <c>max_fee_ratio</c>, and Bitcoin Core message signatures with wallet keys.
/// </summary>
public partial class WalletPsbtServiceTests
{
    private static readonly Key s_foreignKey = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    [Fact]
    public async Task Given_AFundedPsbt_When_SignedWithoutFinalizing_Then_EachWalletInputCarriesItsSignature()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 30_000);
        AddWalletUtxo(AddressType.P2Tr, 1, 30_000);
        var funded = await _service.FundPsbtAsync(Request(50_000), Ct);

        // Act
        var signed = await _service.SignPsbtAsync(funded.Psbt, Ct);

        // Assert: a partial signature on the P2WPKH input, the key path signature on the P2TR one, nothing finalized
        Assert.Equal([0u, 1u], signed.SignedInputs);
        var psbt = PSBT.Load(signed.SignedPsbt, Network.RegTest);
        Assert.All(psbt.Inputs, i => Assert.Null(i.FinalScriptWitness));
        Assert.Single(psbt.Inputs, i => i.PartialSigs.Count == 1);
        Assert.Single(psbt.Inputs, i => i.TaprootKeySignature is not null);

        // Any finalizer can now complete it, and every input verifies
        Assert.Single(psbt.Inputs, i => i.HDKeyPaths.Count == 1);
        Assert.Single(psbt.Inputs, i => i.TaprootInternalKey is not null && i.HDTaprootKeyPaths.Count == 1);
        psbt.Finalize();
        var tx = psbt.ExtractTransaction();
        var validator = tx.CreateValidator(psbt.Inputs.Select(i => i.WitnessUtxo!).ToArray());
        for (var i = 0; i < tx.Inputs.Count; i++)
            Assert.True(validator.ValidateInput(i).Error is null or ScriptError.OK);
    }

    [Fact]
    public async Task Given_AMixedPsbt_When_SignedThenFinalizedAndPublished_Then_OnlyOurInputIsOursAndItIsCollaborative()
    {
        // Arrange: a foreign P2WPKH input first, our leased output second
        var (utxo, outPoint, ourOutput) = AddWalletUtxo(AddressType.P2Wpkh, 0, 60_000);
        await _service.LeaseAsync(s_lockId, utxo.TxId, utxo.Index, TimeSpan.Zero, Ct);
        var (foreignOutPoint, foreignOutput) = ForeignOutput(40_000);
        var psbt = MixedPsbt(foreignOutPoint, foreignOutput, outPoint);

        // Act: we sign ours, the other party signs and finalizes theirs, we finalize
        var signed = await _service.SignPsbtAsync(psbt.ToBytes(), Ct);
        var afterUs = PSBT.Load(signed.SignedPsbt, Network.RegTest);
        var foreignUntouched = afterUs.Inputs[0].PartialSigs.Count == 0 && afterUs.Inputs[0].FinalScriptWitness is null;
        afterUs.SignWithKeys(s_foreignKey);
        afterUs.Inputs[0].FinalizeInput();
        var finalized = await _service.FinalizePsbtAsync(afterUs.ToBytes(), Ct);
        var published = await _service.PublishAsync(finalized.RawFinalTx, "with a friend", Ct);

        // Assert
        Assert.Equal([1u], signed.SignedInputs);
        Assert.True(foreignUntouched);
        var tx = Transaction.Load(finalized.RawFinalTx, Network.RegTest);
        var validator = tx.CreateValidator([foreignOutput, ourOutput]);
        Assert.True(validator.ValidateInput(0).Error is null or ScriptError.OK);
        Assert.True(validator.ValidateInput(1).Error is null or ScriptError.OK);
        Assert.True(published);
        var row = Assert.Single(_published);
        Assert.Equal(BroadcastPurpose.WalletCollaborative, row.Purpose);
        Assert.Null(row.Fee);
        Assert.Equal("with a friend", row.Label);
    }

    [Fact]
    public async Task Given_AMixedPsbtWhoseOtherInputIsNotFinalized_When_Finalizing_Then_Refused()
    {
        // Arrange
        var (utxo, outPoint, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 60_000);
        await _service.LeaseAsync(s_lockId, utxo.TxId, utxo.Index, TimeSpan.Zero, Ct);
        var (foreignOutPoint, foreignOutput) = ForeignOutput(40_000);

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(
                    () => _service.FinalizePsbtAsync(MixedPsbt(foreignOutPoint, foreignOutput, outPoint).ToBytes(), Ct));

        // Assert: we must be the last signer (LND)
        Assert.Equal(WalletPsbtError.FailedPrecondition, e.Error);
        Assert.Contains("is not finalized", e.Message);
    }

    [Fact]
    public async Task Given_AnUnleasedWalletInput_When_SigningAPsbt_Then_RefusedAndNothingSigned()
    {
        // Arrange
        var (_, outPoint, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(() => _service.SignPsbtAsync(UnsignedPsbt(outPoint), Ct));

        // Assert
        Assert.Equal(WalletPsbtError.FailedPrecondition, e.Error);
        Assert.Contains("not leased", e.Message);
    }

    [Fact]
    public async Task Given_APsbtWithoutWalletInputs_When_Signed_Then_ItComesBackUnchanged()
    {
        // Arrange
        var (foreignOutPoint, foreignOutput) = ForeignOutput(40_000);
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(foreignOutPoint));
        tx.Outputs.Add(Money.Satoshis(30_000), s_destination);
        var psbt = PSBT.FromTransaction(tx, Network.RegTest);
        psbt.Inputs[0].WitnessUtxo = foreignOutput;

        // Act
        var signed = await _service.SignPsbtAsync(psbt.ToBytes(), Ct);

        // Assert
        Assert.Empty(signed.SignedInputs);
        Assert.Equal(psbt.ToBytes(), signed.SignedPsbt);
    }

    [Fact]
    public async Task Given_AP2TrChangeType_When_FundingAPsbt_Then_TheChangeIsP2TrAndPaysItsLargerOutput()
    {
        // Arrange
        var p2trChange = GetP2TrExtKey(60, true).Neuter().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86,
                                                                           Network.RegTest);
        _walletService.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Tr, true))
                      .ReturnsAsync(new WalletAddressModel(AddressType.P2Tr, 60, true, p2trChange.ToString()));
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var p2wpkh = await _service.FundPsbtAsync(Request(40_000), Ct);
        await _service.ReleaseAsync(s_lockId, p2wpkh.Leases[0].TxId, p2wpkh.Leases[0].Index, Ct);

        // Act
        var p2tr = await _service.FundPsbtAsync(Request(40_000) with { ChangeAddressType = AddressType.P2Tr }, Ct);

        // Assert: the P2TR change pays its 48 more weight units at the request's 1,000 sat/kw
        var tx = PSBT.Load(p2tr.Psbt, Network.RegTest).GetGlobalTransaction();
        Assert.Equal(p2trChange.ScriptPubKey, tx.Outputs[p2tr.ChangeOutputIndex].ScriptPubKey);
        Assert.Equal(p2wpkh.Fee.Satoshi + 48, p2tr.Fee.Satoshi);
    }

    [Fact]
    public async Task Given_AMaxFeeRatio_When_TheFeeIsAboveIt_Then_RefusedAndNothingLeased()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(
                    () => _service.FundPsbtAsync(Request(40_000) with { MaxFeeRatio = 0.001 }, Ct));
        var allowed = await _service.FundPsbtAsync(Request(40_000) with { MaxFeeRatio = 0.2 }, Ct);

        // Assert
        Assert.Equal(WalletPsbtError.FailedPrecondition, e.Error);
        Assert.StartsWith("fee 0.00000", e.Message);
        Assert.Contains("on total output value 0.0004 BTC with max fee ratio of 0.001", e.Message);
        Assert.Single(allowed.Leases);
    }

    [Theory]
    [InlineData(AddressType.P2Wpkh)]
    [InlineData(AddressType.P2Tr)]
    public void Given_AWalletAddress_When_AMessageIsSigned_Then_ItVerifiesAgainstTheAddressAsLndDoes(AddressType type)
    {
        // Arrange
        var signer = CreateSigner();
        var key = type == AddressType.P2Wpkh ? GetP2WpkhExtKey(3, false) : GetP2TrExtKey(3, false);
        var address = key.Neuter().PubKey.GetAddress(type == AddressType.P2Wpkh
                                                         ? ScriptPubKeyType.Segwit
                                                         : ScriptPubKeyType.TaprootBIP86, Network.RegTest);
        var message = "hello lnd"u8.ToArray();

        // Act
        var signature = signer.SignWalletMessage(new WalletAddressModel(type, 3, false, address.ToString()), message);
        var verified = BitcoinMessageSignature.Verify(address, message, signature);
        var otherMessage = BitcoinMessageSignature.Verify(address, "hello"u8.ToArray(), signature);

        // Assert: valid, the recovered key is the address's (untweaked) key, compressed header
        Assert.Equal(65, signature.Length);
        Assert.InRange(signature[0], (byte)31, (byte)34);
        Assert.NotNull(verified);
        Assert.True(verified.Value.Valid);
        Assert.Equal(key.Neuter().PubKey.ToBytes(), verified.Value.PubKey);
        Assert.False(otherMessage?.Valid ?? false);
    }

    [Fact]
    public void Given_AWrongAddressIndex_When_SigningAMessage_Then_TheSignerRefuses()
    {
        // Arrange: index 4's key does not give index 3's address
        var signer = CreateSigner();
        var address = GetP2WpkhExtKey(3, false).Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignWalletMessage(
                                           new WalletAddressModel(AddressType.P2Wpkh, 4, false, address.ToString()),
                                           [1, 2, 3]));
    }

    [Fact]
    public void Given_BitcoinCoresSignMessageVector_When_Verified_Then_ValidAndOurSignatureIsTheSame()
    {
        // Arrange: Bitcoin Core's functional test rpc_signmessage.py (signmessagewithprivkey, a legacy address)
        var secret = new BitcoinSecret("cUeKHd5orzT3mz8P9pxyREHfsWtVfgsfDjiZZBcjUBAaGk1BTj7N", Network.RegTest);
        var message = "This is just a test message"u8.ToArray();
        const string expected = "INbVnW4e6PeRmsv2Qgu8NuopvrVjkcxob+sX8OcZG0SALhWybUjzMLPdAsXI46YZGb0KQTRii+wWIQzRpG/U+S0=";
        var address = BitcoinAddress.Create("mpLQjfK79b7CCV4VMJWEWAj5Mpx8Up5zxB", Network.RegTest);

        // Act
        var verified = BitcoinMessageSignature.Verify(address, message, Convert.FromBase64String(expected));
        var ours = LightningMessageSignature.Sign(secret.PrivateKey.ToBytes(), BitcoinMessageSignature.Digest(message));

        // Assert
        Assert.Equal(secret.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.RegTest), address);
        Assert.True(verified?.Valid);
        Assert.Equal(expected, Convert.ToBase64String(ours));
    }

    private LocalLightningSigner CreateSigner() =>
        new(Mock.Of<IFundingOutputBuilder>(), Mock.Of<Domain.Protocol.Interfaces.IKeyDerivationService>(),
            NullLogger<LocalLightningSigner>.Instance, new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest },
            _keyManager.Object, _utxos);

    private static (OutPoint OutPoint, TxOut Output) ForeignOutput(long amountSat) =>
        (new OutPoint(RandomUtils.GetUInt256(), 7),
         new TxOut(Money.Satoshis(amountSat), s_foreignKey.PubKey.WitHash.ScriptPubKey));

    private static PSBT MixedPsbt(OutPoint foreign, TxOut foreignOutput, OutPoint ours)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(foreign));
        tx.Inputs.Add(new TxIn(ours));
        tx.Outputs.Add(Money.Satoshis(95_000), s_destination);
        var psbt = PSBT.FromTransaction(tx, Network.RegTest);
        psbt.Inputs[0].WitnessUtxo = foreignOutput;
        return psbt;
    }
}