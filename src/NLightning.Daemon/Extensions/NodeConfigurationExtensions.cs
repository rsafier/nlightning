using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace NLightning.Daemon.Extensions;

using Domain.Node.Options;
using Domain.Protocol.Constants;
using Helpers;
using Utilities;

public static class NodeConfigurationExtensions
{
    public const string DefaultNetwork = "mainnet";

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

    public static (IConfiguration, string, string) ReadInitialConfiguration(string[] args)
    {
        // Map -n/-c and turn bare flags into --flag=true, so they don't swallow the next argument
        args = DaemonUtils.NormalizeArgs(args);

        // Get network from the command line or environment variable first
        var initialConfig = new ConfigurationBuilder()
                           .AddCommandLine(args)
                           .Add(new NltgEnvironmentVariablesSource())
                           .Build();
        var network = initialConfig["network"] ?? DefaultNetwork;

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

            network = initialConfig["Node:Network"] ?? DefaultNetwork;
        }

        // If no custom path, use default ~/.nltg/{network}/appsettings.json
        if (!usingCustomConfig)
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configPath = Path.Combine(homeDir, ".nltg", network);
            configFile = Path.Combine(configPath, "appsettings.json");

            // Ensure directory exists
            Directory.CreateDirectory(configPath);

            // Create default config if none exists
            if (!File.Exists(configFile))
                File.WriteAllText(configFile, CreateDefaultConfigJson(network));
        }

        // Log startup info using bootstrap logger
        Log.Information("Starting NLTG with configuration from {ConfigPath} (Network: {Network})", configPath, network);

        // Build configuration with proper precedence
        var config = new ConfigurationBuilder();
        config.Sources.Clear();

        config.AddJsonFile(configFile!, optional: false, reloadOnChange: false);

        // The default config dir is chosen by network, so the file must not contradict it
        if (!usingCustomConfig)
        {
            var fileNetwork = new ConfigurationBuilder().AddJsonFile(configFile!, optional: false,
                                                                     reloadOnChange: false)
                                                        .Build()["Node:Network"];
            if (!string.IsNullOrEmpty(fileNetwork) && !fileNetwork.Equals(network, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{configFile} sets Node:Network to '{fileNetwork}', but it is in the '{network}' directory. " +
                    $"Move {configPath} to {Path.Combine(Path.GetDirectoryName(configPath)!, fileNetwork)} and " +
                    $"start with --network {fileNetwork}, or change Node:Network in the file to '{network}'.");

            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Node:Network"] = network });
        }

        var configuration = config
                           .Add(new NltgEnvironmentVariablesSource())
                           .AddCommandLine(args)
                           .Build();

        return (configuration, network, configPath!);
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
    /// <remarks>
    /// <c>Node:EnableHtlcs</c> is written explicitly: true on regtest, false elsewhere (the same as leaving it unset,
    /// see <see cref="NodeOptions.HtlcsEnabled"/>), so the switch is visible. <c>Node:Routing</c> carries every
    /// <see cref="RoutingOptions"/> default except <see cref="RoutingOptions.HtlcMaximumMsat"/> (unset: the channel's
    /// own limits only).
    /// </remarks>
    internal static string CreateDefaultConfigJson(string network)
    {
        var routing = new RoutingOptions();
        var enableHtlcs = string.Equals(network, NetworkConstants.Regtest, StringComparison.OrdinalIgnoreCase);
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
                   "Network": "{{NETWORK}}",
                   "Daemon": false,
                   "DnsSeedServers": [
                     "nlseed.nlightn.ing",
                     "nodes.lightning.directory",
                     "lseed.bitcoinstats.com"
                   ],
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
                 "FeeEstimation": {
                   "Url": "https://mempool.space/api/v1/fees/recommended",
                   "Method": "GET",
                   "ContentType": "application/json",
                   "PreferredFeeRate": "fastestFee",
                   "CacheExpiration": "5m",
                   "RateMultiplier": 250,
                   "CacheFile": "fee_estimation_cache.bin"
                 },
                 "Database": {
                   "Provider": "Sqlite",
                   "ConnectionString": "Data Source=nltg.db;Cache=Shared",
                   "RunMigrations": false,
                   "EnableSensitiveQueryLogging": false
                 },
                 "Bitcoin": {
                   "RpcEndpoint": "http://localhost:8332",
                   "RpcUser": "bitcoinrpc",
                   "RpcPassword": "your_rpc_password",
                   "ZmqHost": "bitcoinzmq",
                   "ZmqBlockPort": 8334,
                   "ZmqTxPort": 8335
                 }
               }
               """.Replace("{{NETWORK}}", network)
                  .Replace("{{ENABLE_HTLCS}}", enableHtlcs ? "true" : "false")
                  .Replace("{{FEE_BASE_MSAT}}", Invariant(routing.FeeBaseMsat))
                  .Replace("{{FEE_PPM}}", Invariant(routing.FeeProportionalMillionths))
                  .Replace("{{CLTV_EXPIRY_DELTA}}", Invariant(routing.CltvExpiryDelta))
                  .Replace("{{MAX_CLTV_EXPIRY_DISTANCE}}", Invariant(routing.MaxCltvExpiryDistance))
                  .Replace("{{EXPIRY_TOO_SOON_BLOCKS}}", Invariant(routing.ExpiryTooSoonBlocks))
                  .Replace("{{INVOICE_MIN_FINAL_CLTV_EXPIRY}}", Invariant(routing.InvoiceMinFinalCltvExpiry))
                  .Replace("{{INVOICE_EXPIRY_SECONDS}}", Invariant(routing.InvoiceExpirySeconds))
                  .Replace("{{HTLC_MINIMUM_MSAT}}", Invariant(routing.HtlcMinimumMsat));

        static string Invariant(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);
    }
}