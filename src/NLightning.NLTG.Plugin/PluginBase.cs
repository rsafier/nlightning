using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.NLTG.Plugin;

public interface IPluginBase
{
    public string Name { get; }
    public string Description { get; }
    public Task Execute(CancellationToken cancellationToken = default);
    
    public void Initialize(IServiceCollection services, IConfiguration config);

    public void Loaded(IServiceProvider provider, IConfiguration config);
}