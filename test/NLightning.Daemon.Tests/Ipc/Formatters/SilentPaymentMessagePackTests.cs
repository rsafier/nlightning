using MessagePack;

namespace NLightning.Daemon.Tests.Ipc.Formatters;

using Domain.Bitcoin.SilentPayments.Models;
using Domain.Client.Enums;
using Domain.Client.Responses;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

public class SilentPaymentMessagePackTests
{
    [Fact]
    public void Given_FullStatusAddressAndLabels_When_MessagePackRoundTrip_Then_AllRecoveryFieldsSurvive()
    {
        // Arrange
        var status = new SilentPaymentStatus(true, true, true, true, 10, 20, 30, 40, 50, 100,
            "RpcVerbosity3", 7, 2, 5, 123.45, "prevout unavailable");
        var wire = SilentPaymentIpcResponse.FromClientResponse(new SilentPaymentClientResponse(
            new SilentPaymentAddressResult("sprt1qaddress", 7, "store", true),
            [new SilentPaymentLabelInfo(7, "store", 10, "sprt1qlabel"),
             new SilentPaymentLabelInfo(0, "change", 10, "sprt1qchange", true)], status));
        // Act
        var bytes = MessagePackSerializer.Serialize(wire, NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken);
        var restored = MessagePackSerializer.Deserialize<SilentPaymentIpcResponse>(bytes, NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken);
        // Assert
        Assert.Equal(("sprt1qaddress", (uint?)7, "store", true),
            (restored.Address, restored.Label, restored.LabelName, restored.RecoverableElsewhere));
        var labels = Assert.IsType<List<SilentPaymentLabelIpcInfo>>(restored.Labels);
        Assert.Equal(2, labels.Count);
        Assert.Equal((7u, "store", 10u, "sprt1qlabel", false),
            (labels[0].M, labels[0].Name, labels[0].CreatedAtHeight, labels[0].Address, labels[0].IsChange));
        Assert.True(labels[1].IsChange);
        var result = Assert.IsType<SilentPaymentStatusIpcInfo>(restored.Status);
        Assert.Equal((true, true, true, true, (uint?)10, (uint?)20, (uint?)30, (uint?)40, (uint?)50,
                      100u, "RpcVerbosity3", 7, 2, 5, (double?)123.45, "prevout unavailable"),
            (result.Enabled, result.Send, result.Receive, result.RecoverableElsewhere, result.BirthdayHeight,
             result.LiveFromHeight, result.LiveCursorHeight, result.RescanCursorHeight, result.RescanTargetHeight,
             result.RecoveryLabelCount, result.PrevoutSource, result.FoundOutputs, result.IgnoredOutputs,
             result.UnspentOutputs, result.LastScanMilliseconds, result.LastError));
    }

    [Fact]
    public void Given_UnspentOutputs_When_RoundTrippedAndPrinted_Then_EachOutpointIsListedForWithdrawUtxo()
    {
        // Arrange (NL-1296: spstatus names the outpoints withdraw --utxo takes)
        const string txid = "154499a7c742719609d35ae6021fb39a4c0f34ce8863ad06ed6988ad861e6fb0";
        var status = new SilentPaymentStatus(true, true, true, true, 10, 20, 30, null, null, 0, "GetBlock", 2, 0, 2,
                                             null, null)
        {
            Unspent = [new SilentPaymentUnspentOutput(txid, 0, 40_000, 3_487_066, 1),
                       new SilentPaymentUnspentOutput(txid, 1, 5_000, 3_487_067, null)]
        };
        var wire = SilentPaymentIpcResponse.FromClientResponse(new SilentPaymentClientResponse(null, null, status));
        var output = new StringWriter();

        // Act
        var bytes = MessagePackSerializer.Serialize(wire, NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken);
        var restored = MessagePackSerializer.Deserialize<SilentPaymentIpcResponse>(bytes, NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken);
        new NLightning.Client.Printers.SilentPaymentPrinter(output).Print(restored);

        // Assert
        var text = output.ToString().ReplaceLineEndings("\n");
        Assert.Contains($"  {txid}:0 40000 sat, block 3487066, label 1\n", text);
        Assert.Contains($"  {txid}:1 5000 sat, block 3487067, no label\n", text);
    }

    [Fact]
    public void Given_RescanRequest_When_MessagePackRoundTrip_Then_RangeAndRecoveryLabelsReachDomain()
    {
        // Arrange
        var request = new SilentPaymentIpcRequest { Label = "store", FromHeight = 123, RecoveryLabels = 100, Cancel = true };
        // Act
        var bytes = MessagePackSerializer.Serialize(request, NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken);
        var restored = MessagePackSerializer.Deserialize<SilentPaymentIpcRequest>(bytes, NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken)
            .ToClientRequest(ClientCommand.SilentPaymentRescan);
        // Assert
        Assert.Equal((ClientCommand.SilentPaymentRescan, "store", (uint?)123, (uint?)100, true),
            (restored.Command, restored.Label, restored.FromHeight, restored.RecoveryLabels, restored.Cancel));
    }
}