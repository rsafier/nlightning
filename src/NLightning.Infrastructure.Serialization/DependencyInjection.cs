using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;
using NLightning.Infrastructure.Protocol.Factories;
using NLightning.Infrastructure.Serialization.Factories;
using NLightning.Infrastructure.Serialization.Interfaces;
using NLightning.Infrastructure.Serialization.Messages;
using NLightning.Infrastructure.Serialization.Node;
using NLightning.Infrastructure.Serialization.Onion;
using NLightning.Infrastructure.Serialization.Tlv;

namespace NLightning.Infrastructure.Serialization;

/// <summary>
/// Extension methods for setting up Bitcoin infrastructure services in an IServiceCollection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds Serialization infrastructure services to the specified IServiceCollection.
    /// </summary>
    /// <param name="services">The IServiceCollection to add services to.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddSerializationInfrastructureServices(this IServiceCollection services)
    {
        // Singleton services (one instance throughout the application)
        services.AddSingleton<IFailureMessageSerializer, FailureMessageSerializer>();
        services.AddSingleton<IFeatureSetSerializer, FeatureSetSerializer>();
        services.AddSingleton<IHopPayloadSerializer, HopPayloadSerializer>();
        services.AddSingleton<IMessageSerializer, MessageSerializer>();
        services.AddSingleton<IMessageTypeSerializerFactory, MessageTypeSerializerFactory>();
        services.AddSingleton<IPayloadSerializerFactory, PayloadSerializerFactory>();
        services.AddSingleton<ITlvSerializer, TlvSerializer>();
        services.AddSingleton<ITlvStreamSerializer, TlvStreamSerializer>();
        services.AddSingleton<IValueObjectSerializerFactory, ValueObjectSerializerFactory>();

        // The TLV serializers depend on the converter factory, which lives in NLightning.Infrastructure. TryAdd keeps
        // this layer self-sufficient without clashing with AddInfrastructureServices.
        services.TryAddSingleton<ITlvConverterFactory, TlvConverterFactory>();

        return services;
    }
}