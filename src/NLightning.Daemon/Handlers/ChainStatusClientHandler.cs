using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// How the node follows the chain (ClientCommand 16, NL-216): whether the chain monitor's block processing is halted
/// (a block that keeps failing, or a reorg deeper than the header ring), why, the last processed block, bitcoind's tip
/// and the operations refused while halted (<see cref="ChainProcessingHalt"/>).
/// </summary>
/// <remarks>
/// bitcoind's tip is best effort: when bitcoind cannot be asked the response says so (null) instead of failing, since
/// an operator asks exactly when something is wrong.
/// </remarks>
public sealed class ChainStatusClientHandler : IClientCommandHandler<ChainStatusClientRequest, ChainStatusClientResponse>
{
    private readonly IBitcoinChainService _bitcoinChainService;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly ILogger<ChainStatusClientHandler> _logger;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ChainStatus;

    public ChainStatusClientHandler(IBitcoinChainService bitcoinChainService, IBlockchainMonitor blockchainMonitor,
                                    ILogger<ChainStatusClientHandler> logger)
    {
        _bitcoinChainService = bitcoinChainService;
        _blockchainMonitor = blockchainMonitor;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ChainStatusClientResponse> HandleAsync(ChainStatusClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var halted = _blockchainMonitor.IsChainProcessingHalted;
        var reason = halted ? _blockchainMonitor.ChainProcessingHaltReason ?? "unknown" : null;

        uint? tip = null;
        try
        {
            tip = await _bitcoinChainService.GetCurrentBlockHeightAsync().WaitAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "chainstatus: could not read bitcoind's tip");
        }

        return new ChainStatusClientResponse(halted, reason, _blockchainMonitor.LastProcessedBlockHeight, tip,
                                             halted ? ChainProcessingHalt.RefusedOperations : []);
    }
}