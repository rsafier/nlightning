using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace NLightning.Daemon.Extensions;

using Application.Accounting;
using Application.Offers.Receive;
using Application.OnionMessages;
using Domain.Accounting.Prices;
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
    private const string FeeCacheFileKey = "FeeEstimation:CacheFile";
    private const string OnionKeyFileKey = "Node:Tor:OnionServiceKeyFile";
    private const string PriceFileKey = "Accounting:Prices:CsvFile";

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
        IConfiguration? fileConfiguration = null;

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
            fileConfiguration = initialConfig;

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
                fileConfiguration = existing;
                RegisterCustomSignet(existing);
                fileNetwork = existing[NodeNetworkKey];
            }
            else
            {
                // An unknown network fails here, before its directory is created
                _ = BitcoinNetwork.Resolve(network);

                // Owner-only: the directory gets the key file, the cookie, the database and the logs, and the file
                // gets the bitcoind RPC password
                FilePermissionUtils.CreateOwnerOnlyDirectory(configPath);
                FilePermissionUtils.WriteNewOwnerOnlyFile(configFile, CreateDefaultConfigJson(network));
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

        // Directories and files written by older builds (or by hand) may be readable by other users
        if (!string.IsNullOrEmpty(configPath))
            FilePermissionUtils.WarnIfAccessibleByOthers(configPath, "The configuration directory", Log.Logger);
        if (!string.IsNullOrEmpty(configFile))
            FilePermissionUtils.WarnIfAccessibleByOthers(configFile, "The configuration file", Log.Logger);

        // Build configuration with proper precedence
        var config = new ConfigurationBuilder();
        config.Sources.Clear();

        config.AddJsonFile(configFile!, optional: false, reloadOnChange: false);

        // The resolved network wins over the file's spelling ("mutinynet" is signet to everything but the directory)
        var overrides = new Dictionary<string, string?> { [NodeNetworkKey] = resolvedNetwork.Name };
        if (BitcoinNetwork.IsCustomSignet(network))
            overrides[CustomSignetNameKey] = network;

        // Relative paths in the file (the template's nltg.db, logs/ and the fee cache) resolve against the
        // configuration directory, not against wherever the daemon was started from (NL-306)
        foreach (var (key, anchoredValue) in AnchorRelativePathsToConfigDirectory(fileConfiguration, configPath!))
            overrides[key] = anchoredValue;

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
    /// The file's relative database, log and fee-cache paths (the template's <c>nltg.db</c>, <c>logs/log-.txt</c> and
    /// <c>fee_estimation_cache.bin</c>), resolved against the configuration directory (NL-306). Absolute paths and
    /// SQLite's special <c>Data Source</c> values (<c>:memory:</c>, a <c>file:</c> URI) are left out, i.e. kept as the
    /// file wrote them.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> AnchorRelativePathsToConfigDirectory(
        IConfiguration? fileConfiguration, string configPath)
    {
        if (fileConfiguration is null)
            yield break;

        // The SQLite database lands next to the key file, the cookie and the backups, not in the working directory
        if (string.Equals(fileConfiguration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase)
            && fileConfiguration["Database:ConnectionString"] is { } connectionString
            && connectionString.Length > 0)
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (builder.DataSource.Length > 0 && !IsAnchoredSqliteDataSource(builder.DataSource))
            {
                builder.DataSource = Path.GetFullPath(Path.Combine(configPath, builder.DataSource));
                yield return new KeyValuePair<string, string?>("Database:ConnectionString", builder.ToString());
            }
        }

        if (fileConfiguration[FeeCacheFileKey] is { } cacheFile && cacheFile.Length > 0
            && !Path.IsPathRooted(cacheFile))
            yield return new KeyValuePair<string, string?>(FeeCacheFileKey,
                                                           Path.GetFullPath(Path.Combine(configPath, cacheFile)));

        // The onion service key sits next to the node key, also when the file does not name it (Node:Tor)
        var onionKeyFile = fileConfiguration[OnionKeyFileKey];
        if (string.IsNullOrWhiteSpace(onionKeyFile))
            onionKeyFile = TorOptions.DefaultOnionServiceKeyFile;
        if (!Path.IsPathRooted(onionKeyFile))
            yield return new KeyValuePair<string, string?>(OnionKeyFileKey,
                                                           Path.GetFullPath(Path.Combine(configPath, onionKeyFile)));

        // The price file of the financial books sits in the configuration directory, also when the file does not name
        // it (NL-602 A3-T2, D-A11: <configPath>/prices.csv)
        var priceFile = fileConfiguration[PriceFileKey];
        if (string.IsNullOrWhiteSpace(priceFile))
            priceFile = AccountingPriceOptions.DefaultCsvFile;
        if (!Path.IsPathRooted(priceFile))
            yield return new KeyValuePair<string, string?>(PriceFileKey,
                                                           Path.GetFullPath(Path.Combine(configPath, priceFile)));

        // The file sinks of Serilog's WriteTo array ("WriteTo": [ { "Name": "File", ... } ]) or named object
        // ("WriteTo": { "File": ... }); both flatten to a section per sink with Args:path
        foreach (var sink in fileConfiguration.GetSection("Serilog:WriteTo").GetChildren())
        {
            if (sink["Args:path"] is { } sinkPath && sinkPath.Length > 0 && !Path.IsPathRooted(sinkPath))
                yield return new KeyValuePair<string, string?>($"{sink.Path}:Args:path",
                                                               Path.GetFullPath(Path.Combine(configPath, sinkPath)));
        }
    }

    private static bool IsAnchoredSqliteDataSource(string dataSource) =>
        dataSource is ":memory:" || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                                || Path.IsPathRooted(dataSource);

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
    /// On mainnet, testnet and testnet4 it is <c>null</c>, which binds as unset: the code default of
    /// <see cref="NodeOptions.HtlcsEnabled"/> applies (the BOLT 5 O6-T4 gate decides it; write true or false to
    /// override). <c>Gossip</c> carries the BOLT 7 mainnet gate (plan D12, G5-T5): <c>Enabled</c> (the graph),
    /// <c>SyncEnabled</c> and <c>RelayEnabled</c> are true on every network (the relay on mainnet since the NL-417 proof), and
    /// <c>AllowPublicChannelsOnMainnet</c> is false; <c>AcceptPublicChannels</c> is true everywhere, so on mainnet the
    /// one switch for public channels (ours and a peer's) is <c>AllowPublicChannelsOnMainnet</c>. Every value equals
    /// the code default; the file makes them visible.
    /// <c>Node:Routing</c> carries every <see cref="RoutingOptions"/> default except
    /// <see cref="RoutingOptions.HtlcMaximumMsat"/> (unset: the channel's own limits only). <c>FeeEstimation</c> reads
    /// mempool.space for the network (mutinynet.com for Mutinynet) in sat/vB, and a fixed rate on regtest.
    /// <c>Bitcoin</c> holds bitcoind's default RPC port for the network; on signets also the ZMQ ports of
    /// <c>docs/agents/MUTINYNET.md</c>. <c>OnionMessages</c> carries the <see cref="OnionMessageOptions"/> defaults
    /// (BOLT 4 onion messages, wave M6); they apply only once <c>Node:Features:OptionOnionMessages</c> is advertised.
    /// <c>Offers</c> carries the <see cref="OfferOptions"/> defaults (BOLT 12 offers, wave B12; NL-454).
    /// <c>Node:Bootstrap</c> (BOLT 10, NL-113) is on on mainnet and off elsewhere (D-B10-1 as reversed on 2026-09-28),
    /// with the network's seeds and the public fallback resolvers (D-B10-7). <c>Node:Tor</c> is off, with Tor's usual
    /// SOCKS5 and control ports and the onion key next to the node key. <c>Accounting</c> (NL-602) carries the feed's
    /// sealer and snapshot defaults, <c>Profile</c> <c>Operational</c> (the financial book is opt-in, D-A5),
    /// <c>CostBasis</c> <c>Fifo</c> (D-A2) and every <see cref="AccountingPriceOptions"/> default under <c>Prices</c>
    /// (D-A1, D-A11: USD, the operator's <c>prices.csv</c> first, then mempool.space's historical price API, through
    /// Tor whenever Tor is on (NL-677); nothing is asked while the financial book has no unvalued posting; plain
    /// <c>http://</c> refused and fetched prices sanity-bounded, NL-678). <c>FeeEstimation:AllowPlainHttp</c> is false
    /// (NL-678).
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
        var onionMessages = new OnionMessageOptions();
        var offers = new OfferOptions();
        var accounting = new AccountingOptions();
        var prices = new AccountingPriceOptions();
        // Regtest and signets switch HTLCs on explicitly; mainnet, testnet and testnet4 leave the switch to NodeOptions'
        // code default (null binds as unset), so the BOLT 5 O6-T4 gate decides them
        var enableHtlcs = resolved == BitcoinNetwork.Regtest || isSignet ? "true" : "null";
        // BOLT 7 plan D12 (decided in wave d12): the graph and gossip sync are on everywhere, mainnet included; the
        // relay of others' gossip too since the NL-417 mainnet relay proof (owner decision 2026-09-28); public channels
        // on mainnet are gated by AllowPublicChannelsOnMainnet alone (AcceptPublicChannels keeps its code default, true)
        const string gossipOn = "true";
        const string gossipRelayOn = "true";

        var (feeSource, feeUrl) = name switch
        {
            NetworkConstants.Regtest => (FeeEstimationOptions.SourceFixed, fees.Url),
            NetworkConstants.Testnet => (FeeEstimationOptions.SourceHttp,
                                         "https://mempool.space/testnet/api/v1/fees/recommended"),
            NetworkConstants.Testnet4 => (FeeEstimationOptions.SourceHttp,
                                          "https://mempool.space/testnet4/api/v1/fees/recommended"),
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
            NetworkConstants.Testnet4 => 48332,
            NetworkConstants.Regtest => 18443,
            NetworkConstants.Signet => 38332,
            _ => 8332
        };
        var (zmqHost, zmqBlockPort, zmqTxPort) = isSignet ? ("127.0.0.1", 28332, 28333) : ("bitcoinzmq", 8334, 8335);
        // BOLT 10 bootstrap (NL-113): on on mainnet only (D-B10-1 as reversed on 2026-09-28); the network's own seeds
        // (mainnet 2, testnet 1, testnet4 1, signet 1 — empty on 2026-09-30 — none on regtest, D-B10-2 and NL-545);
        // the public fallback resolvers (D-B10-7)
        var bootstrap = new BootstrapOptions();
        var bootstrapSeeds = string.Join(", ", BootstrapOptions.GetDefaultSeeds(resolved).Select(seed => $"\"{seed}\""));
        var bootstrapFallbackServers = string.Join(", ", bootstrap.FallbackNameServers.Select(server => $"\"{server}\""));
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
                   "Bootstrap": {
                     "Enabled": {{BOOTSTRAP_ENABLED}},
                     "Seeds": [{{BOOTSTRAP_SEEDS}}],
                     "AllowSeedsOnThisNetwork": false,
                     "NameServers": [],
                     "FallbackToPublicResolvers": true,
                     "FallbackNameServers": [{{BOOTSTRAP_FALLBACK_SERVERS}}],
                     "Transport": "{{BOOTSTRAP_TRANSPORT}}",
                     "TorNameServer": "soa.nodes.lightning.directory:53",
                     "MinPeers": {{BOOTSTRAP_MIN_PEERS}},
                     "MaxPeersFromBootstrap": {{BOOTSTRAP_MAX_PEERS}},
                     "MaxPerSeed": {{BOOTSTRAP_MAX_PER_SEED}},
                     "MaxDialConcurrency": {{BOOTSTRAP_DIAL_CONCURRENCY}},
                     "PerSeedTimeout": "{{BOOTSTRAP_PER_SEED_TIMEOUT}}",
                     "QueryTimeout": "{{BOOTSTRAP_QUERY_TIMEOUT}}",
                     "ConnectTimeout": "{{BOOTSTRAP_CONNECT_TIMEOUT}}",
                     "RetryInterval": "{{BOOTSTRAP_RETRY_INTERVAL}}",
                     "MaxRuns": {{BOOTSTRAP_MAX_RUNS}},
                     "MaintenanceInterval": "{{BOOTSTRAP_MAINTENANCE_INTERVAL}}",
                     "MaxMaintenanceBackoff": "{{BOOTSTRAP_MAX_MAINTENANCE_BACKOFF}}",
                     "FailedEndpointTtl": "{{BOOTSTRAP_FAILED_ENDPOINT_TTL}}",
                     "StartupDelay": "{{BOOTSTRAP_STARTUP_DELAY}}",
                     "AddressFamilies": "{{BOOTSTRAP_FAMILIES}}",
                     "AllowNonRoutableAddresses": false,
                     "UseQueryConditions": false
                   },
                   "ListenAddresses": [
                     "0.0.0.0:9735"
                   ],
                   "Tor": {
                     "Mode": "Off",
                     "SocksProxy": "{{TOR_SOCKS}}",
                     "StreamIsolation": true,
                     "ConnectTimeout": "00:01:00",
                     "Control": "{{TOR_CONTROL}}",
                     "ControlPassword": null,
                     "ControlCookieFile": null,
                     "AllowUnauthenticatedControlPort": false,
                     "OnionServiceEnabled": null,
                     "OnionServicePort": 9735,
                     "OnionServiceTarget": null,
                     "OnionServiceKeyFile": "{{TOR_KEY_FILE}}",
                     "OnionServiceClientAuthKeys": [],
                     "OnionServicePoWEnabled": null,
                     "OnionServicePoWQueueRate": null,
                     "OnionServicePoWQueueBurst": null,
                     "AnnounceOnionService": true,
                     "AllowClearnetListen": false
                   },
                   "Features": {
                     "AllowExperimentalFeatures": false
                   },
                   "EnableHtlcs": {{ENABLE_HTLCS}},
                   "MaxAcceptedToSelfDelay": 2016,
                   "MinAcceptedMaxHtlcValueInFlightPercent": 1,
                   "MaxAcceptedChannelReservePercent": 10,
                   "MinCommitmentFeeRatePerKw": 275,
                   "Routing": {
                     "FeeBaseMsat": {{FEE_BASE_MSAT}},
                     "FeeProportionalMillionths": {{FEE_PPM}},
                     "CltvExpiryDelta": {{CLTV_EXPIRY_DELTA}},
                     "MaxCltvExpiryDistance": {{MAX_CLTV_EXPIRY_DISTANCE}},
                     "ExpiryTooSoonBlocks": {{EXPIRY_TOO_SOON_BLOCKS}},
                     "InvoiceMinFinalCltvExpiry": {{INVOICE_MIN_FINAL_CLTV_EXPIRY}},
                     "InvoiceExpirySeconds": {{INVOICE_EXPIRY_SECONDS}},
                     "HtlcMinimumMsat": {{HTLC_MINIMUM_MSAT}}
                   },
                   "Keysend": {
                     "Accept": true,
                     "FinalCltvExpiryDelta": 40
                   },
                   "Quiescence": {
                     "Timeout": "00:01:00",
                     "IdleTimeout": "00:05:00"
                   },
                   "DualFund": {
                     "AcceptContributionSat": 0,
                     "MatchOpenerContribution": true,
                     "OpenTimeout": "00:02:00",
                     "AllowRbf": true
                   }
                 },
                 "Splice": {
                   "RotateFundingKey": true,
                   "MinFeeratePerKw": 253,
                   "MaxFeeratePerKw": 250000,
                   "RequireConfirmedInputs": false,
                   "MinRbfBlocks": 1,
                   "MaxRbfAttempts": 8,
                   "MaxRbfFeeShareSatoshis": 50000,
                   "AutoBumpAfterBlocks": 0,
                   "AutoBumpMaxFeeratePerKw": 25000,
                   "AutoBumpMaxFeeSat": 100000,
                   "AutoBumpMaxWait": "00:02:00"
                 },
                 "Shutdown": {
                   "DrainOnSignalSeconds": 0
                 },
                 "Gossip": {
                   "Enabled": {{GOSSIP_ON}},
                   "SyncEnabled": {{GOSSIP_ON}},
                   "RelayEnabled": {{GOSSIP_RELAY_ON}},
                   "AcceptPublicChannels": true,
                   "AllowPublicChannelsOnMainnet": false,
                   "MaxMemoryMb": 1024,
                   "MaxOutboxGossipPerPeer": 10000,
                   "MaxOutboxGossipBytesPerPeer": 4194304
                 },
                 "OnionMessages": {
                   "MaxOutboxPerPeer": {{OM_MAX_OUTBOX}},
                   "MaxQueuedMessages": {{OM_MAX_QUEUED}},
                   "MaxQueuedHandlerWork": {{OM_MAX_HANDLER_WORK}},
                   "MaxPendingReplies": {{OM_MAX_PENDING_REPLIES}},
                   "ReplyTimeout": "{{OM_REPLY_TIMEOUT}}",
                   "MaxPathHops": {{OM_MAX_PATH_HOPS}},
                   "BlindedPathDummyHops": {{OM_DUMMY_HOPS}},
                   "PeerBytesPerSecond": {{OM_PEER_BPS}},
                   "PeerBurstBytes": {{OM_PEER_BURST}},
                   "PeerMessagesPerSecond": {{OM_PEER_MPS}},
                   "PeerBurstMessages": {{OM_PEER_BURST_MSGS}},
                   "GlobalBytesPerSecond": {{OM_GLOBAL_BPS}},
                   "GlobalBurstBytes": {{OM_GLOBAL_BURST}},
                   "GlobalMessagesPerSecond": {{OM_GLOBAL_MPS}},
                   "GlobalBurstMessages": {{OM_GLOBAL_BURST_MSGS}},
                   "ConnectToReply": false
                 },
                 "Offers": {
                   "InvoiceRelativeExpirySeconds": {{OF_INVOICE_EXPIRY}},
                   "MaxPaymentPaths": {{OF_MAX_PAYMENT_PATHS}},
                   "MaxOfferPaths": {{OF_MAX_OFFER_PATHS}},
                   "MaxUnpaidInvoicesPerOffer": {{OF_MAX_UNPAID_PER_OFFER}},
                   "MaxUnpaidInvoices": {{OF_MAX_UNPAID}},
                   "InvoiceRequestsPerSecondPerOffer": {{OF_IRQ_PER_OFFER}},
                   "InvoiceRequestsPerSecond": {{OF_IRQ}},
                   "PathLifetimeMarginBlocks": {{OF_PATH_MARGIN}},
                   "ExpiredInvoicePruneInterval": "{{OF_PRUNE_INTERVAL}}",
                   "ExpiredInvoicePruneBatchSize": {{OF_PRUNE_BATCH}},
                   "ExpiredInvoicePruneGrace": "{{OF_PRUNE_GRACE}}"
                 },
                 "Accounting": {
                   "Enabled": true,
                   "SealInterval": "{{AC_SEAL_INTERVAL}}",
                   "SealBatchSize": {{AC_SEAL_BATCH}},
                   "SnapshotInterval": "{{AC_SNAPSHOT_INTERVAL}}",
                   "Profile": "{{AC_PROFILE}}",
                   "CostBasis": "{{AC_COST_BASIS}}",
                   "Prices": {
                     "Currency": "{{AC_PRICE_CURRENCY}}",
                     "Source": "{{AC_PRICE_SOURCE}}",
                     "Url": "{{AC_PRICE_URL}}",
                     "AllowPlainHttp": false,
                     "MaxPriceJumpFactor": {{AC_PRICE_MAX_JUMP}},
                     "CsvFile": "{{AC_PRICE_CSV}}",
                     "MaxAge": "{{AC_PRICE_MAX_AGE}}",
                     "FetchInterval": "{{AC_PRICE_FETCH_INTERVAL}}",
                     "MaxFetchesPerRound": {{AC_PRICE_MAX_FETCHES}}
                   }
                 },
                 "FeeEstimation": {
                   "Source": "{{FEE_SOURCE}}",
                   "Url": "{{FEE_URL}}",
                   "AllowPlainHttp": false,
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
                  .Replace("{{BOOTSTRAP_ENABLED}}", bootstrap.IsEnabledOn(resolved) ? "true" : "false")
                  .Replace("{{BOOTSTRAP_SEEDS}}", bootstrapSeeds)
                  .Replace("{{BOOTSTRAP_FALLBACK_SERVERS}}", bootstrapFallbackServers)
                  .Replace("{{BOOTSTRAP_TRANSPORT}}", bootstrap.Transport.ToString())
                  .Replace("{{BOOTSTRAP_MIN_PEERS}}", Invariant(bootstrap.MinPeers))
                  .Replace("{{BOOTSTRAP_MAX_PEERS}}", Invariant(bootstrap.MaxPeersFromBootstrap))
                  .Replace("{{TOR_SOCKS}}", TorOptions.DefaultSocksProxy)
                  .Replace("{{TOR_CONTROL}}", TorOptions.DefaultControl)
                  .Replace("{{TOR_KEY_FILE}}", TorOptions.DefaultOnionServiceKeyFile)
                  .Replace("{{BOOTSTRAP_MAX_PER_SEED}}", Invariant(bootstrap.MaxPerSeed))
                  .Replace("{{BOOTSTRAP_DIAL_CONCURRENCY}}", Invariant(bootstrap.MaxDialConcurrency))
                  .Replace("{{BOOTSTRAP_PER_SEED_TIMEOUT}}", Invariant(bootstrap.PerSeedTimeout))
                  .Replace("{{BOOTSTRAP_QUERY_TIMEOUT}}", Invariant(bootstrap.QueryTimeout))
                  .Replace("{{BOOTSTRAP_CONNECT_TIMEOUT}}", Invariant(bootstrap.ConnectTimeout))
                  .Replace("{{BOOTSTRAP_RETRY_INTERVAL}}", Invariant(bootstrap.RetryInterval))
                  .Replace("{{BOOTSTRAP_MAX_RUNS}}", Invariant(bootstrap.MaxRuns))
                  .Replace("{{BOOTSTRAP_MAINTENANCE_INTERVAL}}", Invariant(bootstrap.MaintenanceInterval))
                  .Replace("{{BOOTSTRAP_MAX_MAINTENANCE_BACKOFF}}", Invariant(bootstrap.MaxMaintenanceBackoff))
                  .Replace("{{BOOTSTRAP_FAILED_ENDPOINT_TTL}}", Invariant(bootstrap.FailedEndpointTtl))
                  .Replace("{{BOOTSTRAP_STARTUP_DELAY}}", Invariant(bootstrap.StartupDelay))
                  .Replace("{{BOOTSTRAP_FAMILIES}}", bootstrap.AddressFamilies.ToString())
                  .Replace("{{ENABLE_HTLCS}}", enableHtlcs)
                  .Replace("{{GOSSIP_ON}}", gossipOn)
                  .Replace("{{GOSSIP_RELAY_ON}}", gossipRelayOn)
                  .Replace("{{FEE_BASE_MSAT}}", Invariant(routing.FeeBaseMsat))
                  .Replace("{{FEE_PPM}}", Invariant(routing.FeeProportionalMillionths))
                  .Replace("{{CLTV_EXPIRY_DELTA}}", Invariant(routing.CltvExpiryDelta))
                  .Replace("{{MAX_CLTV_EXPIRY_DISTANCE}}", Invariant(routing.MaxCltvExpiryDistance))
                  .Replace("{{EXPIRY_TOO_SOON_BLOCKS}}", Invariant(routing.ExpiryTooSoonBlocks))
                  .Replace("{{INVOICE_MIN_FINAL_CLTV_EXPIRY}}", Invariant(routing.InvoiceMinFinalCltvExpiry))
                  .Replace("{{INVOICE_EXPIRY_SECONDS}}", Invariant(routing.InvoiceExpirySeconds))
                  .Replace("{{HTLC_MINIMUM_MSAT}}", Invariant(routing.HtlcMinimumMsat))
                  .Replace("{{OM_MAX_OUTBOX}}", Invariant(onionMessages.MaxOutboxPerPeer))
                  .Replace("{{OM_MAX_QUEUED}}", Invariant(onionMessages.MaxQueuedMessages))
                  .Replace("{{OM_MAX_HANDLER_WORK}}", Invariant(onionMessages.MaxQueuedHandlerWork))
                  .Replace("{{OM_MAX_PENDING_REPLIES}}", Invariant(onionMessages.MaxPendingReplies))
                  .Replace("{{OM_REPLY_TIMEOUT}}", Invariant(onionMessages.ReplyTimeout))
                  .Replace("{{OM_MAX_PATH_HOPS}}", Invariant(onionMessages.MaxPathHops))
                  .Replace("{{OM_DUMMY_HOPS}}", Invariant(onionMessages.BlindedPathDummyHops))
                  .Replace("{{OM_PEER_BPS}}", Invariant(onionMessages.PeerBytesPerSecond))
                  .Replace("{{OM_PEER_BURST}}", Invariant(onionMessages.PeerBurstBytes))
                  .Replace("{{OM_PEER_MPS}}", Invariant(onionMessages.PeerMessagesPerSecond))
                  .Replace("{{OM_PEER_BURST_MSGS}}", Invariant(onionMessages.PeerBurstMessages))
                  .Replace("{{OM_GLOBAL_BPS}}", Invariant(onionMessages.GlobalBytesPerSecond))
                  .Replace("{{OM_GLOBAL_BURST}}", Invariant(onionMessages.GlobalBurstBytes))
                  .Replace("{{OM_GLOBAL_MPS}}", Invariant(onionMessages.GlobalMessagesPerSecond))
                  .Replace("{{OM_GLOBAL_BURST_MSGS}}", Invariant(onionMessages.GlobalBurstMessages))
                  .Replace("{{OF_INVOICE_EXPIRY}}", Invariant(offers.InvoiceRelativeExpirySeconds))
                  .Replace("{{OF_MAX_PAYMENT_PATHS}}", Invariant(offers.MaxPaymentPaths))
                  .Replace("{{OF_MAX_OFFER_PATHS}}", Invariant(offers.MaxOfferPaths))
                  .Replace("{{OF_MAX_UNPAID_PER_OFFER}}", Invariant(offers.MaxUnpaidInvoicesPerOffer))
                  .Replace("{{OF_MAX_UNPAID}}", Invariant(offers.MaxUnpaidInvoices))
                  .Replace("{{OF_IRQ_PER_OFFER}}", Invariant(offers.InvoiceRequestsPerSecondPerOffer))
                  .Replace("{{OF_IRQ}}", Invariant(offers.InvoiceRequestsPerSecond))
                  .Replace("{{OF_PATH_MARGIN}}", Invariant(offers.PathLifetimeMarginBlocks))
                  .Replace("{{OF_PRUNE_INTERVAL}}", Invariant(offers.ExpiredInvoicePruneInterval))
                  .Replace("{{OF_PRUNE_BATCH}}", Invariant(offers.ExpiredInvoicePruneBatchSize))
                  .Replace("{{AC_SEAL_INTERVAL}}", Invariant(accounting.SealInterval))
                  .Replace("{{AC_SEAL_BATCH}}", Invariant(accounting.SealBatchSize))
                  .Replace("{{AC_SNAPSHOT_INTERVAL}}", Invariant(accounting.SnapshotInterval))
                  .Replace("{{AC_PROFILE}}", accounting.Profile.ToString())
                  .Replace("{{AC_COST_BASIS}}", accounting.CostBasis.ToString())
                  .Replace("{{AC_PRICE_CURRENCY}}", prices.Currency)
                  .Replace("{{AC_PRICE_SOURCE}}", prices.Source.ToString())
                  .Replace("{{AC_PRICE_URL}}", prices.Url)
                  .Replace("{{AC_PRICE_CSV}}", prices.CsvFile)
                  .Replace("{{AC_PRICE_MAX_AGE}}", Invariant(prices.MaxAge))
                  .Replace("{{AC_PRICE_FETCH_INTERVAL}}", Invariant(prices.FetchInterval))
                  .Replace("{{AC_PRICE_MAX_FETCHES}}", Invariant(prices.MaxFetchesPerRound))
                  .Replace("{{AC_PRICE_MAX_JUMP}}", Invariant(prices.MaxPriceJumpFactor))
                  .Replace("{{OF_PRUNE_GRACE}}", Invariant(offers.ExpiredInvoicePruneGrace))
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