namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;

public sealed class SilentPaymentClientHandler : IClientCommandHandler<SilentPaymentClientRequest, SilentPaymentClientResponse>
{
    private readonly ISilentPaymentService _service;

    public ClientCommand Command => ClientCommand.GetSilentPaymentAddress;

    public SilentPaymentClientHandler(ISilentPaymentService service) => _service = service;

    public async Task<SilentPaymentClientResponse> HandleAsync(SilentPaymentClientRequest request, CancellationToken ct)
    {
        try
        {
            return request.Command switch
            {
                ClientCommand.GetSilentPaymentAddress => new(Address: await _service.GetAddressAsync(request.Label, ct)),
                ClientCommand.SilentPaymentLabels => new(Labels: await _service.ListLabelsAsync(ct)),
                ClientCommand.SilentPaymentStatus => new(Status: await _service.GetStatusAsync(ct)),
                ClientCommand.SilentPaymentRescan when request.Cancel && request.FromHeight is null && request.RecoveryLabels is null =>
                    new(Status: await _service.CancelRescanAsync(ct)),
                ClientCommand.SilentPaymentRescan when !request.Cancel && request.FromHeight is { } height =>
                    new(Status: await _service.StartRescanAsync(height, request.RecoveryLabels, ct)),
                _ => throw new ClientException(ErrorCodes.InvalidOperation, "Specify --from-height <height> [--labels <count>], or --cancel.")
            };
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }
}