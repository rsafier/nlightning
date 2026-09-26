using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace NLightning.Daemon.Extensions;

using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;
using Helpers;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Services;
using Utilities;

public static class NodeConfigurationExtensions
{
    public const string DefaultNetwork = "mainnet";

    private const string NodeNetworkKey = "Node:Network";
    private const string CustomSignetNameKey = "Node:CustomSignet:Name";

    /// <summary>
    /// Configures the host builder with NLTG configuration and Serilog
    /// </summary>
    public static IHostBuilder ConfigureNltg(this IHostBuilder hostBuilder, IConfiguration configuration)
    {
        // Configure the host builder
        return hostBuilder
              .ConfigureAppConfiguration(builder =>
               {
                   builder.AddConfiguration(configuration);
               })
              .UseSerilog((_, _, loggerConfig) =>
               {
                   // Read from the current configuration
                   loggerConfig
                      .ReadFrom.Configuration(configuration)
                      .Enrich.With<ClassNameEnricher>();
               });
    }

    /// <summary>
    /// Configures the host builder with NLTG configuration and Serilog
    /// </summary>
    public static IHostBuilder ConfigureNltg(this IHostBuilder hostBuilder, string[] args)
    {
        var (config, _, _) = ReadInitialConfiguration(args);

        // Configure the host builder
        return hostBuilder
              .ConfigureAppConfiguration(builder =>
               {
                   builder.AddConfiguration(config);
               })
              .UseSerilog((_, _, loggerConfig) =>
               {
                   // Read from the current configuration
                   loggerConfig
                      .ReadFrom.Configuration(config)
                      .Enrich.With<ClassNameEnricher>();
               });
    }

    /// <summary>
    /// Reads the node's configuration: the file (the <c>--config</c> path, or <c>~/.nltg/&lt;network&gt;</c>), then the
    /// <c>NLTG_</c> environment variables, then the command line.
    /// </summary>
    /// <returns>The configuration, the resolved network name (a built-in network: a custom signet such as
    /// <c>mutinynet</c> gives <c>signet</c>) and the configuration directory.</returns>
    /// <remarks>
    /// The network name is resolved with <see cref="BitcoinNetwork.Resolve"/> and an unknown one fails before anything
    /// is written. A custom signet is named by <c>--network &lt;name&gt;</c> (Mutinynet is known; another name is
    /// accepted when its directory's file has <c>Node:CustomSignet:Name</c>) or by <c>Node:CustomSignet:Name</c> with
    /// <c>Node:Network</c> <c>signet</c>; its directory is <c>~/.nltg/&lt;name&gt;</c>, and the configuration gets
    /// <c>Node:Network</c> <c>signet</c> and <c>Node:CustomSignet:Name</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">The network is unknown.</exception>
    /// <exception cref="InvalidOperationException">The file's network contradicts its directory.</exception>
    public static (IConfiguration, string, string) ReadInitialConfiguration(string[] args)
    {
        // Map -n/-c and turn bare flags into --flag=true, so they don't swallow the next argument
        args = DaemonUtils.NormalizeArgs(args);

        // Get network from the command line or environment variable first
        var initialConfig = new ConfigurationBuilder()
                           .AddCommandLine(args)
                           .Add(new NltgEnvironmentVariablesSource())
                           .Build();
        var network = (initialConfig["network"] ?? DefaultNetwork).Trim().ToLowerInvariant();

        // Check for a custom config path first
        var configPath = initialConfig["config"];
        var configFile = configPath;
        var usingCustomConfig = !string.IsNullOrEmpty(configPath);

        if (usingCustomConfig)
        {
            configPath = Path.GetFullPath(configPath!);
            if (!configPath.EndsWith("json", StringComparison.OrdinalIgnoreCase))
                configFile = Path.Combine(configPath, "appsettings.json");
            else
                configPath = Path.GetDirectoryName(configPath);

            if (!File.Exists(configFile))
            {
                Log.Warning("Custom configuration file not found at {configFile}", configFile);
                usingCustomConfig = false;
            }

            initialConfig = new ConfigurationBuilder()
                           .AddJsonFile(configFile!, optional: false, reloadOnChange: false)
                           .Build();

            RegisterCustomSignet(initialConfig);
            network = (initialConfig[NodeNetworkKey] ?? DefaultNetwork).Trim().ToLowerInvariant();
        }

        // If no custom path, use default ~/.nltg/{network}/appsettings.json
        string? fileNetwork = null;
        if (!usingCustomConfig)
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configPath = Path.Combine(homeDir, ".nltg", network);
            configFile = Path.Combine(configPath, "appsettings.json");

            if (File.Exists(configFile))
            {
                var existing = new ConfigurationBuilder().AddJsonFile(configFile, optional: false,
                                                                      reloadOnChange: false)
                                                         .Build();
                RegisterCustomSignet(existing);
                fileNetwork = existing[NodeNetworkKey];
            }
            else
            {
                // An unknown network fails here, before its directory is created
                _ = BitcoinNetwork.Resolve(network);

                Directory.CreateDirectory(configPath);
                File.WriteAllText(configFile, CreateDefaultConfigJson(network));
            }
        }

        var resolvedNetwork = BitcoinNetwork.Resolve(network);

        // The default config dir is chosen by network, so the file must not contradict it
        if (!usingCustomConfig && !string.IsNullOrEmpty(fileNetwork)
                               && !IsSameNetwork(fileNetwork, network, resolvedNetwork))
            throw new InvalidOperationException(
                $"{configFile} sets Node:Network to '{fileNetwork}', but it is in the '{network}' directory. " +
                $"Move {configPath} to {Path.Combine(Path.GetDirectoryName(configPath)!, fileNetwork)} and " +
                $"start with --network {fileNetwork}, or change Node:Network in the file to '{network}'.");

        // Log startup info using bootstrap logger
        Log.Information("Starting NLTG with configuration from {ConfigPath} (Network: {Network})", configPath, network);

        // Build configuration with proper precedence
        var config = new ConfigurationBuilder();
        config.Sources.Clear();

        config.AddJsonFile(configFile!, optional: false, reloadOnChange: false);

        // The resolved network wins over the file's spelling ("mutinynet" is signet to everything but the directory)
        var overrides = new Dictionary<string, string?> { [NodeNetworkKey] = resolvedNetwork.Name };
        if (BitcoinNetwork.IsCustomSignet(network))
            overrides[CustomSignetNameKey] = network;
        config.AddInMemoryCollection(overrides);

        var configuration = config
                           .Add(new NltgEnvironmentVariablesSource())
                           .AddCommandLine(args)
                           .Build();

        return (configuration, resolvedNetwork.Name, configPath!);
    }

    /// <summary>
    /// Registers the file's <c>Node:CustomSignet:Name</c>, if any, so its name resolves to signet.
    /// </summary>
    private static void RegisterCustomSignet(IConfiguration fileConfiguration)
    {
        var customSignetName = fileConfiguration[CustomSignetNameKey];
        if (string.IsNullOrWhiteSpace(customSignetName))
            return;

        new CustomSignetOptions { Name = customSignetName }.Register();
    }

    /// <summary>
    /// True when the file's <c>Node:Network</c> is the directory's network: the same name, or the same resolved
    /// network (a <c>mutinynet</c> directory whose file says <c>signet</c>).
    /// </summary>
    private static bool IsSameNetwork(string fileNetwork, string directoryNetwork, BitcoinNetwork resolvedNetwork)
    {
        if (fileNetwork.Equals(directoryNetwork, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return BitcoinNetwork.IsCustomSignet(directoryNetwork)
                && BitcoinNetwork.Resolve(fileNetwork) == resolvedNetwork;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <c>NLTG_</c> environment variables, without <c>NLTG_PASSWORD</c>: the key password is not configuration and
    /// must not end up in it.
    /// </summary>
    private sealed class NltgEnvironmentVariablesSource : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new NltgEnvironmentVariablesProvider();
    }

    private sealed class NltgEnvironmentVariablesProvider() : EnvironmentVariablesConfigurationProvider("NLTG_")
    {
        public override void Load()
        {
            base.Load();
            Data.Remove("PASSWORD");
        }
    }

    /// <summary>
    /// Creates default configuration JSON
    /// </summary>
    /// <param name="network">A built-in network or a custom signet name (e.g. <c>mutinynet</c>, which writes
    /// <c>Node:Network</c> <c>signet</c> and <c>Node:CustomSignet:Name</c> <c>mutinynet</c>).</param>
    /// <remarks>
    /// <c>Node:EnableHtlcs</c> is always present so the switch is visible: true on regtest and signets (test coins).
    /// On mainnet and testnet it is <c>null</c>, which binds as unset: the code default of
    /// <see cref="NodeOptions.HtlcsEnabled"/> applies (the BOLT 5 O6-T4 gate decides it; write true or false to
    /// override). <c>Gossip</c> carries the BOLT 7 mainnet gate (plan D12, G5-T5): <c>Enabled</c> (the graph),
    /// <c>SyncEnabled</c> and <c>RelayEnabled</c> are false on mainnet and true elsewhere, and
    /// <c>AllowPublicChannelsOnMainnet</c> is false; <c>AcceptPublicChannels</c> is true everywhere, so on mainnet the
    /// one switch for public channels (ours and a peer's) is <c>AllowPublicChannelsOnMainnet</c>. Every value equals
    /// the code default; the file makes them visible.
    /// <c>Node:Routing</c> carries every <see cref="RoutingOptions"/> default except
    /// <see cref="RoutingOptions.HtlcMaximumMsat"/> (unset: the channel's own limits only). <c>FeeEstimation</c> reads
    /// mempool.space for the network (mutinynet.com for Mutinynet) in sat/vB, and a fixed rate on regtest.
    /// <c>Bitcoin</c> holds bitcoind's default RPC port for the network; on signets also the ZMQ ports of
    /// <c>docs/agents/MUTINYNET.md</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">The network is unknown.</exception>
    internal static string CreateDefaultConfigJson(string network)
    {
        var name = network.Trim().ToLowerInvariant();
        var resolved = BitcoinNetwork.Resolve(name);
        var isSignet = resolved.IsSignet;
        var customSignetName = BitcoinNetwork.IsCustomSignet(name) ? name : string.Empty;
        var routing = new RoutingOptions();
        var fees = new FeeEstimationOptions();
        var isMainnet = resolved == BitcoinNetwork.Mainnet;
        // Regtest and signets switch HTLCs on explicitly; mainnet and testnet leave the switch to NodeOptions' code
        // default (null binds as unset), so the BOLT 5 O6-T4 gate decides both
        var enableHtlcs = resolved == BitcoinNetwork.Regtest || isSignet ? "true" : "null";
        // BOLT 7 plan D12: the graph, gossip sync and relay stay off on mainnet until Proof G5; public channels there
        // are gated by AllowPublicChannelsOnMainnet alone (AcceptPublicChannels keeps its code default, true)
        var gossipOn = isMainnet ? "false" : "true";

        var (feeSource, feeUrl) = name switch
        {
            NetworkConstants.Regtest => (FeeEstimationOptions.SourceFixed, fees.Url),
            NetworkConstants.Testnet => (FeeEstimationOptions.SourceHttp,
                                         "https://mempool.space/testnet/api/v1/fees/recommended"),
            NetworkConstants.Signet => (FeeEstimationOptions.SourceHttp,
                                        "https://mempool.space/signet/api/v1/fees/recommended"),
            NetworkConstants.Mutinynet => (FeeEstimationOptions.SourceHttp,
                                           "https://mutinynet.com/api/v1/fees/recommended"),
            _ when isSignet => (FeeEstimationOptions.SourceHttp, "https://mempool.space/signet/api/v1/fees/recommended"),
            _ => (FeeEstimationOptions.SourceHttp, fees.Url)
        };

        var rpcPort = resolved.Name switch
        {
            NetworkConstants.Testnet => 18332,
            NetworkConstants.Regtest => 18443,
            NetworkConstants.Signet => 38332,
            _ => 8332
        };
        var (zmqHost, zmqBlockPort, zmqTxPort) = isSignet ? ("127.0.0.1", 28332, 28333) : ("bitcoinzmq", 8334, 8335);
        // DNS seeds list mainnet nodes; none are known for signets
        var dnsSeeds = isSignet
                           ? string.Empty
                           : " \"nlseed.nlightn.ing\", \"nodes.lightning.directory\", \"lseed.bitcoinstats.com\" ";
        var customSignet = isSignet
                               ? $"\n    \"CustomSignet\": {{ \"Name\": \"{customSignetName}\" }},"
                               : string.Empty;

        return """
               {
                 "Serilog": {
                   "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.File" ],
                   "MinimumLevel": {
                     "Default": "Error",
                     "Override": {
                       "NLightning": "Information",
                       "System": "Warning"
                     }
                   },
                   "WriteTo": [
                     {
                       "Name": "Console",
                       "Args": {
                         "outputTemplate": "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] [{ClassName}] {Message:lj}{NewLine}{Exception}"
                       }
                     },
                     {
                       "Name": "File",
                       "Args": {
                         "path": "logs/log-.txt",
                         "rollingInterval": "Month",
                         "retainedFileCountLimit": 12,
                         "outputTemplate": "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] [{ClassName}] {Message:lj}{NewLine}{Exception}"
                       }
                     }
                   ],
                   "Enrich": [ "FromLogContext", "WithMachineName", "WithThreadId", "ClassName" ]
                 },
                 "Node": {
                   "Network": "{{NETWORK}}",{{CUSTOM_SIGNET}}
                   "Daemon": false,
                   "DnsSeedServers": [{{DNS_SEEDS}}],
                   "ListenAddresses": [
                     "0.0.0.0:9735"
                   ],
                   "Features": {
                     "AllowExperimentalFeatures": false
                   },
                   "EnableHtlcs": {{ENABLE_HTLCS}},
                   "Routing": {
                     "FeeBaseMsat": {{FEE_BASE_MSAT}},
                     "FeeProportionalMillionths": {{FEE_PPM}},
                     "CltvExpiryDelta": {{CLTV_EXPIRY_DELTA}},
                     "MaxCltvExpiryDistance": {{MAX_CLTV_EXPIRY_DISTANCE}},
                     "ExpiryTooSoonBlocks": {{EXPIRY_TOO_SOON_BLOCKS}},
                     "InvoiceMinFinalCltvExpiry": {{INVOICE_MIN_FINAL_CLTV_EXPIRY}},
                     "InvoiceExpirySeconds": {{INVOICE_EXPIRY_SECONDS}},
                     "HtlcMinimumMsat": {{HTLC_MINIMUM_MSAT}}
                   }
                 },
                 "Gossip": {
                   "Enabled": {{GOSSIP_ON}},
                   "SyncEnabled": {{GOSSIP_ON}},
                   "RelayEnabled": {{GOSSIP_ON}},
                   "AcceptPublicChannels": true,
                   "AllowPublicChannelsOnMainnet": false
                 },
                 "FeeEstimation": {
                   "Source": "{{FEE_SOURCE}}",
                   "Url": "{{FEE_URL}}",
                   "Method": "GET",
                   "ContentType": "application/json",
                   "PreferredFeeRate": "fastestFee",
                   "RateUnit": "{{FEE_RATE_UNIT}}",
                   "ConfirmationTarget": {{FEE_CONF_TARGET}},
                   "EstimateMode": "{{FEE_ESTIMATE_MODE}}",
                   "FixedFeeRatePerKw": {{FEE_FIXED}},
                   "FallbackFeeRatePerKw": {{FEE_FALLBACK}},
                   "CacheExpiration": "5m",
                   "CacheFile": "fee_estimation_cache.bin"
                 },
                 "Database": {
                   "Provider": "Sqlite",
                   "ConnectionString": "Data Source=nltg.db;Cache=Shared",
                   "RunMigrations": false,
                   "EnableSensitiveQueryLogging": false
                 },
                 "Bitcoin": {
                   "RpcEndpoint": "http://localhost:{{RPC_PORT}}",
                   "RpcUser": "bitcoinrpc",
                   "RpcPassword": "your_rpc_password",
                   "ZmqHost": "{{ZMQ_HOST}}",
                   "ZmqBlockPort": {{ZMQ_BLOCK_PORT}},
                   "ZmqTxPort": {{ZMQ_TX_PORT}}
                 }
               }
               """.Replace("{{NETWORK}}", resolved.Name)
                  .Replace("{{CUSTOM_SIGNET}}", customSignet)
                  .Replace("{{DNS_SEEDS}}", dnsSeeds)
                  .Replace("{{ENABLE_HTLCS}}", enableHtlcs)
                  .Replace("{{GOSSIP_ON}}", gossipOn)
                  .Replace("{{FEE_BASE_MSAT}}", Invariant(routing.FeeBaseMsat))
                  .Replace("{{FEE_PPM}}", Invariant(routing.FeeProportionalMillionths))
                  .Replace("{{CLTV_EXPIRY_DELTA}}", Invariant(routing.CltvExpiryDelta))
                  .Replace("{{MAX_CLTV_EXPIRY_DISTANCE}}", Invariant(routing.MaxCltvExpiryDistance))
                  .Replace("{{EXPIRY_TOO_SOON_BLOCKS}}", Invariant(routing.ExpiryTooSoonBlocks))
                  .Replace("{{INVOICE_MIN_FINAL_CLTV_EXPIRY}}", Invariant(routing.InvoiceMinFinalCltvExpiry))
                  .Replace("{{INVOICE_EXPIRY_SECONDS}}", Invariant(routing.InvoiceExpirySeconds))
                  .Replace("{{HTLC_MINIMUM_MSAT}}", Invariant(routing.HtlcMinimumMsat))
                  .Replace("{{FEE_SOURCE}}", feeSource)
                  .Replace("{{FEE_URL}}", feeUrl)
                  .Replace("{{FEE_RATE_UNIT}}", FeeRateConverter.SatPerVByte)
                  .Replace("{{FEE_CONF_TARGET}}", Invariant(fees.ConfirmationTarget))
                  .Replace("{{FEE_ESTIMATE_MODE}}", fees.EstimateMode)
                  .Replace("{{FEE_FIXED}}", Invariant(fees.FixedFeeRatePerKw))
                  .Replace("{{FEE_FALLBACK}}", Invariant(fees.FallbackFeeRatePerKw))
                  .Replace("{{RPC_PORT}}", Invariant(rpcPort))
                  .Replace("{{ZMQ_HOST}}", zmqHost)
                  .Replace("{{ZMQ_BLOCK_PORT}}", Invariant(zmqBlockPort))
                  .Replace("{{ZMQ_TX_PORT}}", Invariant(zmqTxPort));

        static string Invariant(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);
    }
}