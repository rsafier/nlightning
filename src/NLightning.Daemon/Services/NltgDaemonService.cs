using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Services;

using Application.Channels.Fees;
using Application.Channels.Safety.Interfaces;
using Application.Payments.Send.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Client.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class NltgDaemonService : BackgroundService
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelFailureService _channelFailureService;
    private readonly IConfiguration _configuration;
    private readonly IFeeService _feeService;
    private readonly IFeeUpdateScheduler _feeUpdateScheduler;
    private readonly IHtlcExpiryMonitor _htlcExpiryMonitor;
    private readonly ILogger<NltgDaemonService> _logger;
    private readonly INamedPipeIpcService _namedPipeIpcService;
    private readonly IPeerManager _peerManager;
    private readonly NodeOptions _nodeOptions;
    private readonly IPaymentOutcomeHandler _paymentOutcomeHandler;
    private readonly ISecureKeyManager _secureKeyManager;

    public NltgDaemonService(IBlockchainMonitor blockchainMonitor, IChannelFailureService channelFailureService,
                             IConfiguration configuration, IFeeService feeService,
                             IFeeUpdateScheduler feeUpdateScheduler, IHtlcExpiryMonitor htlcExpiryMonitor,
                             ILogger<NltgDaemonService> logger, INamedPipeIpcService namedPipeIpcService,
                             IOptions<NodeOptions> nodeOptions, IPaymentOutcomeHandler paymentOutcomeHandler,
                             IPeerManager peerManager, ISecureKeyManager secureKeyManager)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelFailureService = channelFailureService;
        _configuration = configuration;
        _feeService = feeService;
        _feeUpdateScheduler = feeUpdateScheduler;
        _htlcExpiryMonitor = htlcExpiryMonitor;
        _logger = logger;
        _namedPipeIpcService = namedPipeIpcService;
        _peerManager = peerManager;
        _nodeOptions = nodeOptions.Value;
        _paymentOutcomeHandler = paymentOutcomeHandler;
        _secureKeyManager = secureKeyManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var network = _configuration["network"] ?? _configuration["n"] ?? _nodeOptions.BitcoinNetwork;
        var isDaemon = _configuration.GetValue<bool?>("daemon")
                    ?? _configuration.GetValue<bool?>("daemon-child")
                    ?? _nodeOptions.Daemon;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("NLTG Daemon started on {Network} network", network);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Running in daemon mode: {IsDaemon}", isDaemon);

            var pubKey = _secureKeyManager.GetNodePubKey();
            _logger.LogDebug("Our PubKey is {pubKey}", pubKey.ToString());
        }

        try
        {
            // Start the fee service
            await _feeService.StartAsync(stoppingToken);

            // Start the peer manager service
            await _peerManager.StartAsync(stoppingToken);

            // Every stored channel is in memory now: settle the payments a crash left without an HTLC id (W2-C)
            await _paymentOutcomeHandler.ReconcileInFlightPaymentsAsync(stoppingToken);

            // Channel safety (N9): fail-the-channel broadcasts (and their resumption), the HTLC deadline monitor and
            // the update_fee rounds of the channels we fund
            _channelFailureService.Start();
            _htlcExpiryMonitor.Start();
            await _feeUpdateScheduler.StartAsync(stoppingToken);

            // Start the blockchain monitor service
            await _blockchainMonitor.StartAsync(_secureKeyManager.HeightOfBirth, stoppingToken);

            // Start the IPC server
            await _namedPipeIpcService.StartAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
                await Task.Delay(1000, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stopping NLTG daemon service");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("NLTG shutdown requested");

        // The safety services and the fee rounds stop before the chain monitor and the peers they use
        await Task.WhenAll(_htlcExpiryMonitor.StopAsync(), _feeUpdateScheduler.StopAsync());
        _channelFailureService.Stop();

        await Task.WhenAll(_blockchainMonitor.StopAsync(), _feeService.StopAsync(), _peerManager.StopAsync(),
                           _namedPipeIpcService.StopAsync(), base.StopAsync(cancellationToken));

        _logger.LogInformation("NLTG daemon service stopped");
    }
}