using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// How the node follows the chain (ClientCommand 16, NL-216): whether the chain monitor's block processing is halted
/// (a block that keeps failing, or a reorg deeper than the header ring), why, the last processed block, bitcoind's tip
/// the operations refused while halted (<see cref="ChainProcessingHalt"/>) and how blocks and the mempool are followed
/// (<see cref="BitcoinOptions.Notifications"/>, NL-1094).
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
    private readonly BitcoinOptions? _bitcoinOptions;
    private readonly NodeOptions? _nodeOptions;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ChainStatus;

    public ChainStatusClientHandler(IBitcoinChainService bitcoinChainService, IBlockchainMonitor blockchainMonitor,
                                    ILogger<ChainStatusClientHandler> logger,
                                    IOptions<BitcoinOptions>? bitcoinOptions = null,
                                    IOptions<NodeOptions>? nodeOptions = null)
    {
        _bitcoinChainService = bitcoinChainService;
        _blockchainMonitor = blockchainMonitor;
        _logger = logger;
        _nodeOptions = nodeOptions?.Value;
        try
        {
            _bitcoinOptions = bitcoinOptions?.Value;
        }
        catch (OptionsValidationException)
        {
            // The node does not start with an invalid Bitcoin section; the status then omits the notification mode
            _bitcoinOptions = null;
        }
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

        var (notifications, mempoolWatch) = DescribeNotifications();
        return new ChainStatusClientResponse(halted, reason, _blockchainMonitor.LastProcessedBlockHeight, tip,
                                             halted ? ChainProcessingHalt.RefusedOperations : [], notifications,
                                             mempoolWatch);
    }

    private (string? Notifications, string? MempoolWatch) DescribeNotifications()
    {
        if (_bitcoinOptions is not { } options)
            return (null, null);

        if (options.Notifications != ChainNotificationMode.Poll)
            return ("zmq", options.IsMempoolWatched ? "zmq rawtx" : "off");

        var interval = _nodeOptions is { } node ? options.GetPollInterval(node.BitcoinNetwork) : options.PollInterval;
        return (interval is { } every ? $"poll every {every:c}" : "poll",
                options.IsMempoolWatched ? "gettxspendingprevout poll" : "off");
    }
}