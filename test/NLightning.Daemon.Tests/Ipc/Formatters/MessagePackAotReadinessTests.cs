using System.Reflection;
using MessagePack;

namespace NLightning.Daemon.Tests.Ipc.Formatters;

using Daemon.Models;
using Transport.Ipc.MessagePack;

/// <summary>
/// NativeAOT readiness of the IPC and fee-cache MessagePack contracts (NL-338). A NativeAOT build cannot emit code, so
/// every formatter the IPC options hand out must be compiled ahead of time (the MessagePack source generator, the
/// built-in formatters or our own), never one MessagePack's dynamic resolvers emit at run time.
/// </summary>
public class MessagePackAotReadinessTests
{
    [Fact]
    public void Given_EveryMessagePackContract_When_ItsFormattersAreResolved_Then_NoneIsEmittedAtRunTime()
    {
        // Arrange
        var resolver = NLightningMessagePackOptions.Options.Resolver;
        var contracts = new[] { typeof(NLightningMessagePackOptions).Assembly, typeof(FeeRateCacheData).Assembly }
                       .SelectMany(assembly => assembly.GetTypes())
                       .Where(type => type.GetCustomAttribute<MessagePackObjectAttribute>() is not null
                                   && !type.IsGenericTypeDefinition)
                       .ToList();
        var types = new HashSet<Type>();
        foreach (var contract in contracts)
            Collect(contract, resolver, types);

        // Act
        var emitted = types.Where(type => resolver.GetFormatterDynamic(type) is not { } formatter
                                       || formatter.GetType().Assembly.IsDynamic)
                           .Select(type => type.FullName)
                           .Order(StringComparer.Ordinal)
                           .ToList();

        // Assert
        Assert.True(contracts.Count > 50, $"Only {contracts.Count} MessagePack contracts found");
        Assert.Empty(emitted);
    }

    /// <summary>
    /// The contract and, recursively, the types of its serialized members, and the element types and generic arguments
    /// that a MessagePack built-in formatter (a collection's, <see cref="Nullable{T}"/>'s) hands on to the resolver.
    /// </summary>
    private static void Collect(Type type, IFormatterResolver resolver, HashSet<Type> types)
    {
        if (!types.Add(type))
            return;

        if ((type.IsArray || type.IsGenericType)
         && resolver.GetFormatterDynamic(type)?.GetType().Assembly == typeof(MessagePackSerializer).Assembly)
        {
            if (type.IsArray)
                Collect(type.GetElementType()!, resolver, types);

            foreach (var argument in type.GetGenericArguments())
                Collect(argument, resolver, types);
        }

        if (type.GetCustomAttribute<MessagePackObjectAttribute>() is null)
            return;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in type.GetProperties(flags).Where(p => p.GetCustomAttribute<KeyAttribute>() is not null))
            Collect(property.PropertyType, resolver, types);

        foreach (var field in type.GetFields(flags).Where(f => f.GetCustomAttribute<KeyAttribute>() is not null))
            Collect(field.FieldType, resolver, types);
    }
}