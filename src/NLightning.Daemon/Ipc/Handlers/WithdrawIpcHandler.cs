using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class WithdrawIpcHandler
    : ClientCommandIpcHandler<WithdrawIpcRequest, WithdrawClientRequest, WithdrawClientResponse, WithdrawIpcResponse>
{
    public override ClientCommand Command => ClientCommand.Withdraw;

    public WithdrawIpcHandler(ILogger<WithdrawIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override WithdrawClientRequest ToClientRequest(WithdrawIpcRequest request) => request.ToClientRequest();

    protected override WithdrawIpcResponse ToIpcResponse(WithdrawClientResponse response) =>
        WithdrawIpcResponse.FromClientResponse(response);
}