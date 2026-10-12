namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;

public sealed class WalletHistoryClientHandler(IWalletHistoryService service)
    : IClientCommandHandler<WalletHistoryClientRequest, WalletHistoryClientResponse>
{
    public ClientCommand Command => ClientCommand.WalletHistory;
    public async Task<WalletHistoryClientResponse> HandleAsync(WalletHistoryClientRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Cancel && (request.FromHeight is not null || request.ToHeight is not null || request.AllowPartial || request.AddressCount != 30))
                throw new ArgumentException("Cancel cannot be combined with rescan bounds.");
            if (request.Cancel) return new(await service.CancelAsync(ct));
            if (request.FromHeight is { } from)
                return new(await service.StartRescanAsync(from, request.ToHeight, request.AllowPartial, request.AddressCount, ct));
            if (request.ToHeight is not null || request.AllowPartial || request.AddressCount != 30)
                throw new ArgumentException("Rescan options require --from-height.");
            return new(await service.GetStatusAsync(ct));
        }
        catch (ArgumentException e) { throw new ClientException(ErrorCodes.InvalidOperation, e.Message); }
        catch (InvalidOperationException e) { throw new ClientException(ErrorCodes.InvalidOperation, e.Message); }
    }
}