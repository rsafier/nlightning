using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Daemon.Contracts.Control;
using NLightning.Daemon.Interfaces;
using NLightning.Daemon.Ipc.Handlers;
using NLightning.Domain.Client.Enums;
using NLightning.Transport.Ipc;
using NLightning.Transport.Ipc.MessagePack;
using NLightning.Transport.Ipc.Responses;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

public sealed class NativeContextNodeInfoTests
{
    [Fact]
    public async Task InfoResponseReportsEnrolledContextWithoutAcceptingARequestSelector()
    {
        var query = new Mock<INodeInfoQueryService>();
        query.Setup(q => q.QueryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new NodeInfoResponse
        {
            NodeId = "node-a",
            OwnerId = "owner-a",
            SignerId = "signer-a",
            PubKey = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798",
            ListeningTo = "127.0.0.1:19735",
            BestBlockHash = new string('0', 64)
        });
        MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options;
        var handler = new NodeInfoIpcHandler(query.Object, NullLogger<NodeInfoIpcHandler>.Instance);
        var response = await handler.HandleAsync(new IpcEnvelope
        {
            Command = ClientCommand.NodeInfo,
            CorrelationId = Guid.NewGuid(),
            Payload = MessagePackSerializer.Serialize(new[] { "node-b", "owner-b", "signer-b" },
                cancellationToken: TestContext.Current.CancellationToken)
        }, TestContext.Current.CancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var info = MessagePackSerializer.Deserialize<NodeInfoIpcResponse>(response.Payload,
            NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken);
        Assert.Equal("node-a", info.NodeId);
        Assert.Equal("owner-a", info.OwnerId);
        Assert.Equal("signer-a", info.SignerId);
    }
}