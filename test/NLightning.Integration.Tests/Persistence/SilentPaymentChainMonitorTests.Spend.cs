using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Repositories.Database.Bitcoin;

public sealed partial class SilentPaymentChainMonitorTests
{
    /// <summary>
    /// NL-1296 (FAFO2, 2026-10-07): a labelled silent payment found by the scanner is an ordinary wallet output after a
    /// restart (the <c>Utxos</c> row, the in-memory set, the confirmed balance). The default selection policy
    /// (<c>SilentPayments:AvoidMixing</c>) leaves it out while ordinary coins pay, and the operator's explicit choice
    /// reserves exactly it, persisted in the same database.
    /// </summary>
    [Fact]
    public async Task Given_ALabelledReceipt_When_RestartedAndChosen_Then_ItIsInTheBalanceAndReservedOnlyByChoice()
    {
        // Arrange: one labelled receipt in block 101, the node restarted three blocks later
        using var keys = new ReceiverKeys();
        var options = EnabledOptions();
        options.RecoveryLabelCount = 1; // scan label 1 as getspaddress --label would register it
        await using var harness = CreateHarness(keys.Manager, options);
        await harness.StartAsync(95);
        var receipt = LabelledReceipt(keys.Manager, 7, 1, AmountSat);
        await harness.MineAndDeliverAsync(receipt);
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: the stored row, the loaded coin and the balance
        await using (var context = harness.Context())
        {
            var row = await new UtxoDbRepository(context).GetByIdAsync(Id(receipt), 0, true);
            Assert.NotNull(row?.SilentPayment);
            Assert.Equal(1u, row.SilentPayment.Label);
            Assert.Equal(AddressType.P2Tr, row.AddressType);
        }

        var memory = Memory(harness);
        Assert.True(memory.TryGetUtxo(Id(receipt), 0, out var coin));
        Assert.True(coin.BacksAnchorReserve(101 + 3));
        Assert.Equal(AmountSat, memory.GetConfirmedBalance(101 + 3).Satoshi);

        // Act: an ordinary coin that pays alone, then a default reservation and an explicit one
        var ordinary = new UtxoModel(new Domain.Bitcoin.ValueObjects.TxId(Enumerable.Repeat((byte)0x31, 32).ToArray()),
                                     0, LightningMoney.Satoshis(500_000), 90,
                                     new WalletAddressModel(AddressType.P2Tr, 0, false,
                                                            "bcrt1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqc8gma6"));
        memory.Add(ordinary);
        var selector = new FeeInputSelector(memory, harness.Services.GetRequiredService<IServiceScopeFactory>(),
                                            Options.Create(new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest }),
                                            NullLogger<FeeInputSelector>.Instance);
        const long rate = 1_000;
        const int extraWeight = 200;
        var target = LightningMoney.Satoshis(AmountSat - (rate * (extraWeight + 231) + 999) / 1_000);
        // (targets that leave no change: the harness registers no wallet service for a change address)
        var byDefault = await selector.ReserveAsync(LightningMoney.Satoshis(500_000 - 431), LightningMoney.Satoshis(rate),
                                                    extraWeight, "withdraw", new WalletSelectionPolicy(),
                                                    TestContext.Current.CancellationToken);
        await selector.ReleaseAsync(byDefault.Id, TestContext.Current.CancellationToken);
        var chosen = await selector.ReserveAsync(target, LightningMoney.Satoshis(rate), extraWeight, "withdraw",
                                                 new WalletSelectionPolicy { Inputs = [(Id(receipt), 0)] },
                                                 TestContext.Current.CancellationToken);

        // Assert: the default kept the silent coin out; the choice reserved exactly it, without change
        Assert.Equal(ordinary.TxId, Assert.Single(byDefault.Inputs).TxId);
        var input = Assert.Single(chosen.Inputs);
        Assert.Equal(Id(receipt), input.TxId);
        Assert.True(input.IsSilentPayment);
        Assert.Equal(0, chosen.ChangeAmount.Satoshi);
        await using (var context = harness.Context())
        {
            var stored = await new FeeInputReservationDbRepository(context).GetByIdAsync(chosen.Id);
            Assert.Equal(Id(receipt), Assert.Single(stored!.Inputs).TxId);
        }

        // A coin already reserved cannot be chosen again
        await Assert.ThrowsAsync<WalletSpendException>(() => selector.ReserveAsync(
            target, LightningMoney.Satoshis(rate), extraWeight, "withdraw",
            new WalletSelectionPolicy { Inputs = [(Id(receipt), 0)] }, TestContext.Current.CancellationToken));
    }

    private static NBitcoin.Transaction LabelledReceipt(Infrastructure.Bitcoin.Managers.SecureKeyManager keys,
                                                        byte seed, uint label, long amountSat)
    {
        // The labelled spend key B_m = B_spend + hash(b_scan || m)·G, as getspaddress --label hands out
        var crypto = new Infrastructure.Bitcoin.Crypto.SilentPayments.SilentPaymentCrypto();
        Assert.True(crypto.TrySumPublicKeys([keys.SpendPubKey, keys.GetLabelPoint(label)], out var labelledSpend));
        using var sender = new NBitcoin.Key(Enumerable.Repeat((byte)0x21, 32).ToArray());
        var previous = new NBitcoin.OutPoint(new NBitcoin.uint256(Enumerable.Repeat(seed, 32).ToArray()), 0);
        var bytes = new byte[36];
        previous.Hash.ToBytes().CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), previous.N);
        var input = new Domain.Bitcoin.SilentPayments.Models.SilentPaymentSenderInput(bytes, sender.ToBytes(), false);
        try
        {
            var derived = crypto.DeriveOutputs([input],
                [new Domain.Bitcoin.SilentPayments.Models.SilentPaymentRecipient(keys.ScanPubKey, labelledSpend)]);
            var transaction = NBitcoin.Network.RegTest.CreateTransaction();
            transaction.Inputs.Add(new NBitcoin.TxIn(previous)
            {
                WitScript = new NBitcoin.WitScript([new byte[71], sender.PubKey.ToBytes()])
            });
            transaction.Outputs.Add(new NBitcoin.TxOut(NBitcoin.Money.Satoshis(amountSat),
                                                       new NBitcoin.Script([0x51, 0x20, .. derived[0].OutputKey32])));
            return transaction;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(input.PrivateKey32!);
        }
    }
}