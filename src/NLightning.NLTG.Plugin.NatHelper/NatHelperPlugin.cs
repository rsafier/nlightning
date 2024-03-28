using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.NLTG.Plugin.NatHelper;

public class NatHelperPlugin : IPluginBase
{
  

    public string Name => "NatHelperPlugin";

    public string Description => "Network Address Translation / UPNP Assistant Plugin";

    public NatHelperPlugin()
    {
        
    }
    
    public Task Execute(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public void Initialize(IServiceCollection services, IConfiguration config)
    {
        services.AddHostedService<NatHelperService>();
    }

    public void Loaded(IServiceProvider provider, IConfiguration config)
    {
        //do nothing
    }
}