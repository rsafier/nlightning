using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NLightning.NLTG;
using NLightning.NLTG.Parsers;
using NLightning.NLTG.Plugin;
using Serilog;
using ServiceStack;

class Program
{
    private static WebApplication _webApplication;

    static void Main(string[] args)
    {
        try
        {
            // Set up the DI container
            var builder = WebApplication.CreateSlimBuilder(args);

            var services = builder.Services; // new ServiceCollection();



           
            services.AddOptions<NLightning.NLTG.Options>();
            // Configure logging to use Serilog
            
            // Initialize the options and logger from provided args
            var (options, loggerConfig) = ArgumentOptionParser.Initialize(args);

            // Check if the configuration file exists
            if (File.Exists(options.ConfigFile))
            {
                // Parse the configuration file
                (options, loggerConfig) = FileOptionParser.MergeWithFile(options.ConfigFile, options, loggerConfig);
            }
            else if (options.IsConfigFileDefault) // Create config with passed args only if it's in the default location
            {
                options.SaveToFile();
            }
            else
            {
                throw new Exception($"Config file not found: {options.ConfigFile}");
            }

            builder.Configuration
                .AddYamlFile(options.ConfigFile, optional: false);
            
          

            Log.Logger = loggerConfig.CreateLogger();
            services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog());

            Log.Logger.Information("Using config file: {ConfigFile}", options.ConfigFile);

            Log.Logger.Information("Loading Plugins...");
            List<string> pluginPaths =  new List<string>()
            {
                @"/Users/rjs/nlightning/src/NLightning.NLTG.Plugin.NatHelper/bin/Debug/net8.0/NLightning.NLTG.Plugin.NatHelper.dll"  
            };
            services.LoadPlugins(builder.Configuration, pluginPaths );
            
            
            // Build the service provider
            Log.Logger.Information("Building Host...");
            _webApplication = builder.Build();

            Log.Logger.Information("Notify Plugins Load Completed...");
            _webApplication.Services.LoadCompletedPlugins(builder.Configuration, pluginPaths);
            
            Log.Logger.Debug("Log file: {LogFile}", options.LogFile);
            _webApplication.Run();
        }
        catch (Exception e)
        {
            Console.WriteLine($"ERROR: {e.Message}");
        }
    }
}