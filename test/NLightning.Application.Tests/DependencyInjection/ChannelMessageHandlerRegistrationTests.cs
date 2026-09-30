using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.DependencyInjection;

using Application;
using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Guards the explicit channel message handler registrations (NL-055): the old registration scanned the assembly with
/// <c>Assembly.GetTypes()</c>, which trimming/AOT may not preserve, so <see cref="DependencyInjection.AddApplicationServices"/>
/// now carries an explicit list. These tests compare that list against a re-run of the old reflection scan on every
/// test run, so a handler implementation added to the assembly without being registered (or a stale registration)
/// fails here and the list cannot rot.
/// </summary>
public class ChannelMessageHandlerRegistrationTests
{
    [Fact]
    public void Given_TheHandlerImplementationsInTheApplicationAssembly_When_AddApplicationServicesRuns_Then_TheRegistrationsMatchTheReflectionScan()
    {
        // Arrange (the old registration behavior, re-run as the oracle for the explicit list)
        var expected = DiscoverHandlerImplementationsByReflection();
        Assert.NotEmpty(expected);

        // Act
        var registered = GetRegisteredHandlers();

        // Assert (count/sanity comparison in both directions: a handler missing from the explicit list fails
        // here, and so does a registration left behind by a deleted handler)
        Assert.Equal(expected.Count, registered.Count);
        var missing = expected.Except(registered).ToList();
        Assert.True(missing.Count == 0,
                    $"Handler implementations not registered by AddApplicationServices: " +
                    string.Join(", ", missing.Select(p => p.HandlerType.Name)));
        var stale = registered.Except(expected).ToList();
        Assert.True(stale.Count == 0,
                    $"Handler registrations without an implementation in the assembly: " +
                    string.Join(", ", stale.Select(p => p.HandlerType!.Name)));
        Assert.Equal(expected, registered);
    }

    [Fact]
    public void Given_TheHandlerRegistrations_When_Inspected_Then_EveryServiceIsItsClosedInterfaceOnceAndScoped()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddApplicationServices();

        // Act
        var descriptors = services
                         .Where(d => d.ServiceType.IsGenericType &&
                                     d.ServiceType.GetGenericTypeDefinition() == typeof(IChannelMessageHandler<>))
                         .ToList();

        // Assert (one registration per closed IChannelMessageHandler<>, always scoped, always the handler class)
        Assert.NotEmpty(descriptors);
        Assert.All(descriptors, d =>
        {
            Assert.Equal(ServiceLifetime.Scoped, d.Lifetime);
            Assert.NotNull(d.ImplementationType);
            Assert.True(d.ImplementationType!.IsClass && !d.ImplementationType.IsAbstract);
            Assert.Contains(d.ServiceType, d.ImplementationType.GetInterfaces());
        });
        Assert.All(descriptors.GroupBy(d => d.ServiceType), g => Assert.Single(g));
    }

    [Fact]
    public void Given_AKnownNormalOperationHandler_When_ItsRegistrationIsLookedUp_Then_ItIsScopedForItsMessage()
    {
        // Arrange (an anchor independent of the reflection oracle above)
        var services = new ServiceCollection();
        services.AddApplicationServices();

        // Act
        var descriptor = services.SingleOrDefault(d =>
            d.ServiceType == typeof(IChannelMessageHandler<CommitmentSignedMessage>));

        // Assert
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Scoped, descriptor!.Lifetime);
        Assert.Equal(typeof(CommitmentSignedMessageHandler), descriptor.ImplementationType);
    }

    /// <summary>
    /// Re-runs the pre-NL-055 discovery (a <c>GetTypes()</c> scan of the Application assembly for
    /// <c>IChannelMessageHandler&lt;&gt;</c> implementations) as the oracle the explicit list is compared against.
    /// </summary>
    private static List<(Type HandlerType, Type ServiceType)> DiscoverHandlerImplementationsByReflection()
    {
        return typeof(DependencyInjection).Assembly.GetTypes()
                       .Where(t => t is { IsClass: true, IsAbstract: false })
                       .Select(t => (HandlerType: t,
                                     ServiceType: t.GetInterfaces()
                                                  .FirstOrDefault(i => i.IsGenericType &&
                                                                       i.GetGenericTypeDefinition() ==
                                                                       typeof(IChannelMessageHandler<>))))
                       .Where(p => p.ServiceType is not null)
                       .Select(p => (p.HandlerType, p.ServiceType!))
                       .OrderBy(p => p.HandlerType.FullName, StringComparer.Ordinal)
                       .ToList();
    }

    private static List<(Type HandlerType, Type ServiceType)> GetRegisteredHandlers()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();

        return services
              .Where(d => d.ServiceType.IsGenericType &&
                          d.ServiceType.GetGenericTypeDefinition() == typeof(IChannelMessageHandler<>))
              .Select(d => (HandlerType: d.ImplementationType!, ServiceType: d.ServiceType))
              .OrderBy(p => p.HandlerType.FullName, StringComparer.Ordinal)
              .ToList();
    }
}