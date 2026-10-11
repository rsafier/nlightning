using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Repositories.Database.Bitcoin;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-302 / NL-304 (found on Mutinynet): a UTXO received before a restart is reloaded from the database into the
/// in-memory UTXO set, locked to a channel and spent by the funding transaction, which the signer must sign.
/// </summary>
public class FundingSigningAfterReloadTests
{
    private static readonly ExtKey s_walletRoot =
        ExtKey.CreateFromSeed(Convert.FromHexString("0f0e0d0c0b0a09080706050403020100f0e0d0c0b0a090807060504030201000"));

    private static readonly Key s_localFundingKey = new(Convert.FromHexString(
                                                            "1111111111111111111111111111111111111111111111111111111111111111"));

    private static readonly Key s_remoteFundingKey = new(Convert.FromHexString(
                                                             "2222222222222222222222222222222222222222222222222222222222222222"));

    private const uint AddressIndex = 4;
    private const long UtxoSats = 100_000;
    private const ulong FundingSats = 90_000;

    [Fact]
    public async Task Given_UtxoReloadedFromDatabase_When_SigningFundingTransaction_Then_EveryInputIsSigned()
    {
        // Arrange: the UTXO comes back through the startup load (GetUnspentAsync(includeWalletAddress: true))
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x5a, 32).ToArray());
        var (utxoMemoryRepository, utxo) = await ReloadUtxoAsync(includeWalletAddress: true);
        utxoMemoryRepository.LockUtxosToSpendOnChannel(LightningMoney.Satoshis(FundingSats), channelId);
        var (signer, unsignedTransaction, prevOut) = CreateSignerAndFundingTransaction(channelId, utxo,
            utxoMemoryRepository);

        // Act
        var allSigned = signer.SignFundingTransaction(channelId, unsignedTransaction);

        // Assert
        Assert.True(allSigned);
        var signedTx = Transaction.Load(unsignedTransaction.RawTxBytes, Network.RegTest);
        Assert.False(signedTx.Inputs[0].WitScript == WitScript.Empty);
        Assert.True(signedTx.Inputs.AsIndexedInputs().Single().VerifyScript(prevOut, out var error), error.ToString());
    }

    [Fact]
    public async Task Given_UtxoWithoutWalletAddress_When_SigningFundingTransaction_Then_ThrowsSignerExceptionNamingInput()
    {
        // Arrange: the load before the NL-302 fix left the wallet address out; the signer used to skip the input and
        // then fail with a NullReferenceException (NL-304)
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x5b, 32).ToArray());
        var (utxoMemoryRepository, utxo) = await ReloadUtxoAsync(includeWalletAddress: false);
        utxoMemoryRepository.LockUtxosToSpendOnChannel(LightningMoney.Satoshis(FundingSats), channelId);
        var (signer, unsignedTransaction, _) = CreateSignerAndFundingTransaction(channelId, utxo,
                                                                                 utxoMemoryRepository);

        // Act
        var exception = Assert.Throws<SignerException>(() => signer.SignFundingTransaction(channelId,
                                                               unsignedTransaction));

        // Assert
        Assert.Contains("input 0", exception.Message);
        Assert.Contains("wallet address", exception.Message);
    }

    [Fact]
    public void Given_InputWithoutLockedUtxo_When_SigningFundingTransaction_Then_ThrowsSignerExceptionNamingInput()
    {
        // Arrange: the funding transaction spends an outpoint that is not locked to the channel
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x5c, 32).ToArray());
        var walletAddress = new WalletAddressModel(AddressType.P2Wpkh, AddressIndex, false, "bcrt1qunused");
        var utxo = SqliteTestDatabase.CreateUtxo(walletAddress, txIdSeed: 9, amountSats: UtxoSats);
        var (signer, unsignedTransaction, _) = CreateSignerAndFundingTransaction(channelId, utxo,
                                                                                 new UtxoMemoryRepository());

        // Act
        var exception = Assert.Throws<SignerException>(() => signer.SignFundingTransaction(channelId,
                                                               unsignedTransaction));

        // Assert
        Assert.Contains("input 0", exception.Message);
    }

    private static async Task<(UtxoMemoryRepository, UtxoModel)> ReloadUtxoAsync(bool includeWalletAddress)
    {
        using var database = new SqliteTestDatabase();
        var walletAddress = new WalletAddressModel(AddressType.P2Wpkh, AddressIndex, false,
                                                   GetDepositKey().PubKey.WitHash.GetAddress(Network.RegTest)
                                                                  .ToString());
        var storedUtxo = SqliteTestDatabase.CreateUtxo(walletAddress, amountSats: UtxoSats);
        await using (var context = database.CreateContext())
        {
            new WalletAddressesDbRepository(context).AddRange([walletAddress]);
            new UtxoDbRepository(context).Add(storedUtxo);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = database.CreateContext();
        var reloaded = (await new UtxoDbRepository(readContext).GetUnspentAsync(includeWalletAddress)).ToList();

        var utxoMemoryRepository = new UtxoMemoryRepository();
        utxoMemoryRepository.Load(reloaded);
        return (utxoMemoryRepository, reloaded.Single());
    }

    private static (LocalLightningSigner, SignedTransaction, TxOut) CreateSignerAndFundingTransaction(
        ChannelId channelId, UtxoModel utxo, IUtxoMemoryRepository utxoMemoryRepository)
    {
        var secureKeyManagerMock = new Mock<ISecureKeyManager>();
        secureKeyManagerMock.Setup(x => x.GetDepositP2WpkhKeyAtIndex(AddressIndex, false))
                            .Returns(new ExtPrivKey(GetDepositExtKey().ToBytes()));

        var signer = new LocalLightningSigner(new FundingOutputBuilder(), Mock.Of<IKeyDerivationService>(),
                                              NullLogger<LocalLightningSigner>.Instance,
                                              new NodeOptions { BitcoinNetwork = "regtest" },
                                              secureKeyManagerMock.Object, utxoMemoryRepository);

        var fundingOutput = new FundingOutputBuilder()
           .Build(new FundingOutputInfo(LightningMoney.Satoshis(FundingSats),
                                        s_localFundingKey.PubKey.ToBytes(), s_remoteFundingKey.PubKey.ToBytes()));
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(new uint256(utxo.TxId), utxo.Index));
        tx.Outputs.Add(fundingOutput.ToTxOut());

        // As ChannelModel.GetSigningInfo does: the funding amount goes in through the implicit msat conversion
        signer.RegisterChannel(channelId, new ChannelSigningInfo(tx.GetHash().ToBytes(), 0,
                                                                 LightningMoney.Satoshis(FundingSats),
                                                                 s_localFundingKey.PubKey.ToBytes(),
                                                                 s_remoteFundingKey.PubKey.ToBytes(), 0));

        var prevOut = new TxOut(Money.Satoshis(UtxoSats), GetDepositKey().PubKey.WitHash.ScriptPubKey);
        return (signer, new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()), prevOut);
    }

    private static ExtKey GetDepositExtKey() => s_walletRoot.Derive(new KeyPath($"84'/1'/0'/0/{AddressIndex}"));

    private static Key GetDepositKey() => GetDepositExtKey().PrivateKey;
}