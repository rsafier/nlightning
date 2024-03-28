using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection; 

namespace NLightning.NLTG.Plugin;

using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
 

public static class ServicePluginExtension
{
  
    public static IServiceCollection LoadPlugins(this IServiceCollection services, IConfiguration config, List<string> pluginPaths)
    {
        if (pluginPaths == null)
        {
            throw new ArgumentNullException(nameof(pluginPaths));
        }

        pluginPaths.ForEach(p =>
        {
            var assembly = Assembly.LoadFrom(p);
            var part = new AssemblyPart(assembly);
            services.AddControllers().PartManager.ApplicationParts.Add(part);

            var assemblyTypes = assembly.GetTypes();
            var pluginClass = assemblyTypes.SingleOrDefault(t => t.GetInterface(nameof(IPluginBase)) != null);

            if (pluginClass != null)
            {
                var initMethod = pluginClass.GetMethod(nameof(IPluginBase.Initialize),
                    BindingFlags.Public | BindingFlags.Instance);
                var obj = Activator.CreateInstance(pluginClass);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                initMethod.Invoke(obj, new object[] { services, config });
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            }
        });

        return services;
    }

    public static void LoadCompletedPlugins(this IServiceProvider services, IConfiguration config, List<string> pluginPaths)
    {
        if (pluginPaths == null)
        {
            throw new ArgumentNullException(nameof(pluginPaths));
        }

        pluginPaths.ForEach(p =>
        {
            var assembly = Assembly.LoadFrom(p);

            var assemblyTypes = assembly.GetTypes();
            var pluginClass = assemblyTypes.SingleOrDefault(t => t.GetInterface(nameof(IPluginBase)) != null);

            if (pluginClass != null)
            {
                var initMethod = pluginClass.GetMethod(nameof(IPluginBase.Loaded),
                    BindingFlags.Public | BindingFlags.Instance);
                var obj = Activator.CreateInstance(pluginClass);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                initMethod.Invoke(obj, new object[] { services, config });
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            }
        });
    }
}