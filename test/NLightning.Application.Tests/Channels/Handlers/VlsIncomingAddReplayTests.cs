using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Application.Channels.Services;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Keysend;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Domain.Signing.Recovery;
using Domain.Signing.Vls;
using static NormalOperationTestContext;

public class VlsIncomingAddReplayTests
{
    [Fact]
    public async Task ExactPendingHolderAddReplayDoesNotChangeStateOrPersistOrCallSigner()
    {
        var (context, original) = CreatePendingAdd();
        var (handler, signer) = CreateHandler(context);
        var before = context.State;

        Assert.Empty(await handler.HandleAsync(Clone(original), ChannelState.Open, new FeatureOptions(), PeerNodeId));

        Assert.Same(before, context.State);
        Assert.Empty(context.Calls);
        Assert.Empty(context.Applied);
        signer.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("hash")]
    [InlineData("expiry")]
    [InlineData("onion")]
    [InlineData("path")]
    [InlineData("missing-path")]
    [InlineData("custom-value")]
    [InlineData("missing-custom")]
    [InlineData("extra-custom")]
    [InlineData("extra-tlv")]
    [InlineData("mutated-tlv")]
    public async Task ChangedPendingHolderAddReplayClosesConnectionWithoutChangingDurableInputs(string mutation)
    {
        var (context, original) = CreatePendingAdd();
        var (handler, signer) = CreateHandler(context);
        var before = context.State;
        var changed = Clone(original, mutation);

        var error = await Assert.ThrowsAsync<ChannelWarningException>(() =>
            handler.HandleAsync(changed, ChannelState.Open, new FeatureOptions(), PeerNodeId));

        Assert.True(error.CloseConnection);
        Assert.Contains("does not match the durable VLS", error.Message);
        Assert.Same(before, context.State);
        Assert.Empty(context.Calls);
        Assert.Empty(context.Applied);
        signer.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("native")]
    [InlineData("absent")]
    [InlineData("blocked")]
    [InlineData("send-commit")]
    public async Task DuplicateAddOutsidePendingVlsHolderValidationRetainsNormalIdRejection(string mode)
    {
        var (context, original) = CreatePendingAdd();
        var (handler, signer) = CreateHandler(context, mode);
        var before = context.State;

        var error = await Assert.ThrowsAsync<ChannelWarningException>(() =>
            handler.HandleAsync(Clone(original), ChannelState.Open, new FeatureOptions(), PeerNodeId));

        Assert.True(error.CloseConnection);
        Assert.Contains("B2-ADD-R07", error.Message);
        Assert.Same(before, context.State);
        Assert.Empty(context.Calls);
        signer.VerifyNoOtherCalls();
    }

    private static (NormalOperationTestContext Context, UpdateAddHtlcMessage Message) CreatePendingAdd()
    {
        var context = new NormalOperationTestContext();
        var onion = Onion.ToArray();
        onion[0] = 7;
        var message = new UpdateAddHtlcMessage(
            new UpdateAddHtlcPayload(LightningMoney.MilliSatoshis(50_000_000UL), TestChannelId, 600, 0,
                                    HashOf(SecretOf(1)), onion),
            new BlindedPathTlv(Point(30)), [new CustomRecord(65_537, [1, 2, 3])]);
        context.SetState(context.State.ReceiveAdd(0, message.Payload.Amount.MilliSatoshi,
            new Hash(message.Payload.PaymentHash.ToArray()), 600, message.Payload.OnionRoutingPacket,
            message.BlindedPathTlv!.PathKey, WireCustomRecordCodec.Encode(message.CustomRecords)).Next);
        return (context, message);
    }

    private static (UpdateAddHtlcMessageHandler Handler, Mock<IVlsChannelSigner> Signer) CreateHandler(
        NormalOperationTestContext context, string mode = "pending")
    {
        var signer = new Mock<IVlsChannelSigner>(MockBehavior.Strict);
        var workflows = new Mock<IRemoteSigningWorkflowCoordinator>(MockBehavior.Strict);
        var descriptor = SigningWorkflowSnapshot.Create(context.Channel, context.State, SigningWorkflowKind.ValidateHolder);
        var workflow = new SigningWorkflow(Guid.NewGuid(), TestChannelId,
            mode == "send-commit" ? SigningWorkflowKind.SendCommit : SigningWorkflowKind.ValidateHolder,
            descriptor.ExpectedLocalCommitmentNumber, descriptor.ExpectedRemoteCommitmentNumber,
            descriptor.SnapshotFingerprint, new byte[33], "regtest", 1,
            mode == "blocked" ? SigningWorkflowState.Blocked : SigningWorkflowState.Pending, 0, 0);
        workflows.Setup(w => w.GetPendingAsync(TestChannelId))
                 .ReturnsAsync(mode == "absent" ? [] : new[] { workflow });
        var transitions = context.CreateTransitions(signingWorkflows: workflows.Object,
            vlsSigner: mode == "native" ? null : signer.Object);
        return (new UpdateAddHtlcMessageHandler(NullLogger<UpdateAddHtlcMessageHandler>.Instance, transitions), signer);
    }

    private static UpdateAddHtlcMessage Clone(UpdateAddHtlcMessage original, string? mutation = null)
    {
        var payload = original.Payload;
        var onion = payload.OnionRoutingPacket.ToArray();
        if (mutation == "onion") onion[^1] ^= 1;
        var records = mutation switch
        {
            "missing-custom" => Array.Empty<CustomRecord>(),
            "custom-value" => [new CustomRecord(65_537, [4, 5])],
            "extra-custom" => [new CustomRecord(65_537, [1, 2, 3]), new CustomRecord(65_539, [4])],
            _ => original.CustomRecords.ToArray()
        };
        var message = new UpdateAddHtlcMessage(
            new UpdateAddHtlcPayload(
                LightningMoney.MilliSatoshis(mutation == "amount" ? payload.Amount.MilliSatoshi + 1 : payload.Amount.MilliSatoshi),
                payload.ChannelId, mutation == "expiry" ? payload.CltvExpiry + 1 : payload.CltvExpiry,
                payload.Id, mutation == "hash" ? HashOf(SecretOf(2)) : payload.PaymentHash, onion),
            mutation == "missing-path" ? null : new BlindedPathTlv(mutation == "path" ? Point(31) : original.BlindedPathTlv!.PathKey),
            records);
        if (mutation == "extra-tlv") message.Extension!.Add(new BaseTlv(new BigSize(65_541), [9]));
        if (mutation == "mutated-tlv") message.Extension!.GetTlvs().Last().Value[0] ^= 1;
        return message;
    }
}