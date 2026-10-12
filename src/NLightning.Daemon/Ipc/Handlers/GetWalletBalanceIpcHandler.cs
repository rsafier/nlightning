using MessagePack;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Money;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Services.Ipc.Factories;
using Transport.Ipc;
using Transport.Ipc.Responses;

internal class GetWalletBalanceIpcHandler : IIpcCommandHandler
{
    private readonly IAnchorReserveService? _anchorReserveService;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly ILogger<GetWalletBalanceIpcHandler> _logger;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;
    public ClientCommand Command => ClientCommand.WalletBalance;

    public GetWalletBalanceIpcHandler(IBlockchainMonitor blockchainMonitor, ILogger<GetWalletBalanceIpcHandler> logger,
                                      IUtxoMemoryRepository utxoMemoryRepository,
                                      IAnchorReserveService? anchorReserveService = null)
    {
        _anchorReserveService = anchorReserveService;
        _blockchainMonitor = blockchainMonitor;
        _logger = logger;
        _utxoMemoryRepository = utxoMemoryRepository;
    }

    public async Task<IpcEnvelope> HandleAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var currentBlockHeight = _blockchainMonitor.LastProcessedBlockHeight;
            var confirmedBalance = _utxoMemoryRepository.GetConfirmedBalance(currentBlockHeight);
            var unconfirmedBalance = _utxoMemoryRepository.GetUnconfirmedBalance(currentBlockHeight);

            // NL-379: the anchors reserve and what a funding may still spend
            var reserve = _anchorReserveService is null
                              ? null
                              : await _anchorReserveService.GetStatusAsync(ct);

            // Create a success response
            var response = new WalletBalanceIpcResponse
            {
                ConfirmedBalance = confirmedBalance,
                UnconfirmedBalance = unconfirmedBalance,
                AnchorReserve = reserve?.RequiredReserve ?? LightningMoney.Zero,
                AnchorsChannelCount = reserve?.AnchorsChannelCount ?? 0,
                AvailableBalance = reserve?.AvailableBalance ?? confirmedBalance,
                SpendableBalance = reserve?.SpendableBalance ?? confirmedBalance
            };

            var payload = MessagePackSerializer.Serialize(response, cancellationToken: ct);
            var respEnvelope = new IpcEnvelope
            {
                Version = envelope.Version,
                Command = envelope.Command,
                CorrelationId = envelope.CorrelationId,
                Kind = IpcEnvelopeKind.Response,
                Payload = payload
            };

            return respEnvelope;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting the wallet balance");
            return IpcErrorFactory.CreateErrorEnvelope(envelope, ErrorCodes.ServerError,
                                                       $"Error getting the wallet balance: {e.Message}");
        }
    }
}