using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>splicein</c> (ClientCommand 33) over IPC.
/// </summary>
internal sealed class SpliceInIpcHandler
    : ClientCommandIpcHandler<SpliceInIpcRequest, SpliceInClientRequest, SpliceClientResponse, SpliceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.SpliceIn;

    public SpliceInIpcHandler(ILogger<SpliceInIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override SpliceInClientRequest ToClientRequest(SpliceInIpcRequest request) => request.ToClientRequest();

    protected override SpliceIpcResponse ToIpcResponse(SpliceClientResponse response) =>
        SpliceIpcResponse.FromClientResponse(response);
}

/// <summary>
/// <c>spliceout</c> (ClientCommand 34) over IPC.
/// </summary>
internal sealed class SpliceOutIpcHandler
    : ClientCommandIpcHandler<SpliceOutIpcRequest, SpliceOutClientRequest, SpliceClientResponse, SpliceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.SpliceOut;

    public SpliceOutIpcHandler(ILogger<SpliceOutIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override SpliceOutClientRequest ToClientRequest(SpliceOutIpcRequest request) =>
        request.ToClientRequest();

    protected override SpliceIpcResponse ToIpcResponse(SpliceClientResponse response) =>
        SpliceIpcResponse.FromClientResponse(response);
}

/// <summary>
/// <c>bumpsplice</c> (ClientCommand 37) over IPC, answered with the splice response.
/// </summary>
internal sealed class BumpSpliceIpcHandler
    : ClientCommandIpcHandler<BumpSpliceIpcRequest, BumpSpliceClientRequest, SpliceClientResponse, SpliceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.BumpSplice;

    public BumpSpliceIpcHandler(ILogger<BumpSpliceIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override BumpSpliceClientRequest ToClientRequest(BumpSpliceIpcRequest request) =>
        request.ToClientRequest();

    protected override SpliceIpcResponse ToIpcResponse(SpliceClientResponse response) =>
        SpliceIpcResponse.FromClientResponse(response);
}