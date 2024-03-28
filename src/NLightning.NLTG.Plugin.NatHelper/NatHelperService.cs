using AmberSystems.UPnP;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NLightning.NLTG.Plugin.NatHelper;

public class NatHelperService(ILogger<NatHelperService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger!.LogInformation("NatHelperService Starting...");
        try
        {
            var client = new UpnpClient();
            var discover = await client.Discover();
        
            logger!.LogInformation("NatHelperService found Public IPs: {@DiscoverResult}",discover);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            logger!.LogWarning("Cannot find UPnP endpoint.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger!.LogInformation("NatHelperService Stopping...");
        return Task.CompletedTask;
    }
}