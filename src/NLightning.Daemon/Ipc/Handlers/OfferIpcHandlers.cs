using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class CreateOfferIpcHandler
    : ClientCommandIpcHandler<CreateOfferIpcRequest, CreateOfferClientRequest, CreateOfferClientResponse,
        CreateOfferIpcResponse>
{
    public override ClientCommand Command => OfferClientCommands.CreateOffer;

    public CreateOfferIpcHandler(ILogger<CreateOfferIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override CreateOfferClientRequest ToClientRequest(CreateOfferIpcRequest request) =>
        request.ToClientRequest();

    protected override CreateOfferIpcResponse ToIpcResponse(CreateOfferClientResponse response) =>
        CreateOfferIpcResponse.FromClientResponse(response);
}

internal sealed class ListOffersIpcHandler
    : ClientCommandIpcHandler<ListOffersIpcRequest, ListOffersClientRequest, ListOffersClientResponse,
        ListOffersIpcResponse>
{
    public override ClientCommand Command => OfferClientCommands.ListOffers;

    public ListOffersIpcHandler(ILogger<ListOffersIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ListOffersClientRequest ToClientRequest(ListOffersIpcRequest request) =>
        request.ToClientRequest();

    protected override ListOffersIpcResponse ToIpcResponse(ListOffersClientResponse response) =>
        ListOffersIpcResponse.FromClientResponse(response);
}

internal sealed class DisableOfferIpcHandler
    : ClientCommandIpcHandler<DisableOfferIpcRequest, DisableOfferClientRequest, DisableOfferClientResponse,
        DisableOfferIpcResponse>
{
    public override ClientCommand Command => OfferClientCommands.DisableOffer;

    public DisableOfferIpcHandler(ILogger<DisableOfferIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override DisableOfferClientRequest ToClientRequest(DisableOfferIpcRequest request) =>
        request.ToClientRequest();

    protected override DisableOfferIpcResponse ToIpcResponse(DisableOfferClientResponse response) =>
        DisableOfferIpcResponse.FromClientResponse(response);
}