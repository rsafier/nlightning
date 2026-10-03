using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>liquidityads</c> (ClientCommand 46, liquidity ads NL-850) over IPC.
/// </summary>
internal sealed class LiquidityAdsIpcHandler
    : ClientCommandIpcHandler<LiquidityAdsIpcRequest, LiquidityAdsClientRequest, LiquidityAdsClientResponse,
        LiquidityAdsIpcResponse>
{
    public override ClientCommand Command => ClientCommand.LiquidityAds;

    public LiquidityAdsIpcHandler(ILogger<LiquidityAdsIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override LiquidityAdsClientRequest ToClientRequest(LiquidityAdsIpcRequest request) =>
        request.ToClientRequest();

    protected override LiquidityAdsIpcResponse ToIpcResponse(LiquidityAdsClientResponse response) =>
        LiquidityAdsIpcResponse.FromClientResponse(response);
}