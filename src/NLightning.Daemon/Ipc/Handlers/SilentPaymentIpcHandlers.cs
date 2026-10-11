using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal abstract class SilentPaymentIpcHandler
    : ClientCommandIpcHandler<SilentPaymentIpcRequest, SilentPaymentClientRequest, SilentPaymentClientResponse, SilentPaymentIpcResponse>
{
    protected SilentPaymentIpcHandler(ILogger logger, IServiceProvider serviceProvider) : base(logger, serviceProvider) { }

    protected override SilentPaymentClientRequest ToClientRequest(SilentPaymentIpcRequest request) => request.ToClientRequest(Command);
    protected override SilentPaymentIpcResponse ToIpcResponse(SilentPaymentClientResponse response) => SilentPaymentIpcResponse.FromClientResponse(response);
}

internal sealed class GetSilentPaymentAddressIpcHandler : SilentPaymentIpcHandler
{
    public override ClientCommand Command => ClientCommand.GetSilentPaymentAddress;

    public GetSilentPaymentAddressIpcHandler(ILogger<GetSilentPaymentAddressIpcHandler> logger, IServiceProvider serviceProvider) : base(logger, serviceProvider) { }
}

internal sealed class SilentPaymentLabelsIpcHandler : SilentPaymentIpcHandler
{
    public override ClientCommand Command => ClientCommand.SilentPaymentLabels;

    public SilentPaymentLabelsIpcHandler(ILogger<SilentPaymentLabelsIpcHandler> logger, IServiceProvider serviceProvider) : base(logger, serviceProvider) { }
}

internal sealed class SilentPaymentRescanIpcHandler : SilentPaymentIpcHandler
{
    public override ClientCommand Command => ClientCommand.SilentPaymentRescan;

    public SilentPaymentRescanIpcHandler(ILogger<SilentPaymentRescanIpcHandler> logger, IServiceProvider serviceProvider) : base(logger, serviceProvider) { }
}

internal sealed class SilentPaymentStatusIpcHandler : SilentPaymentIpcHandler
{
    public override ClientCommand Command => ClientCommand.SilentPaymentStatus;

    public SilentPaymentStatusIpcHandler(ILogger<SilentPaymentStatusIpcHandler> logger, IServiceProvider serviceProvider) : base(logger, serviceProvider) { }
}