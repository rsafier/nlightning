using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace NLightning.Daemon.Extensions;

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
                           .AddEnvironmentVariables("NLTG_")
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
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Node:Network"] = network });

        var configuration = config
                           .AddEnvironmentVariables("NLTG_")
                           .AddCommandLine(args)
                           .Build();

        return (configuration, network, configPath!);
    }

    /// <summary>
    /// Creates default configuration JSON
    /// </summary>
    internal static string CreateDefaultConfigJson(string network)
    {
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
                   }
                 },
                 "FeeEstimation": {
                   "Url": "https://mempool.space/api/v1/fees/recommended",
                   "Method": "GET",
                   "ContentType": "application/json",
                   "PreferredFeeRate": "fastestFee",
                   "CacheExpiration": "5m",
                   "RateMultiplier": 1000,
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
               """.Replace("{{NETWORK}}", network);
    }
}