using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class WalletHistoryIpcHandler(ILogger<WalletHistoryIpcHandler> logger, IServiceProvider serviceProvider)
    : ClientCommandIpcHandler<WalletHistoryIpcRequest, WalletHistoryClientRequest, WalletHistoryClientResponse, WalletHistoryIpcResponse>(logger, serviceProvider)
{
    public override ClientCommand Command => ClientCommand.WalletHistory;
    protected override WalletHistoryClientRequest ToClientRequest(WalletHistoryIpcRequest request) => request.ToClientRequest();
    protected override WalletHistoryIpcResponse ToIpcResponse(WalletHistoryClientResponse response) => WalletHistoryIpcResponse.FromClientResponse(response);
}