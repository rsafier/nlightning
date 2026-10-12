using System.Reflection;
using System.Runtime.CompilerServices;

namespace NLightning.Daemon.Tests.Extensions;

using Daemon.Extensions;

/// <summary>
/// The configuration binding source generator binds the node's options in every build (NL-338). It binds into the
/// options instance the options factory already created, so it never sets an init-only member: such a key would be read
/// by the old reflection binder and silently ignored now (and in the NativeAOT build). These tests read the types the
/// generator binds from its generated code in the daemon assembly.
/// </summary>
public class ConfigurationBindingAotTests
{
    [Fact]
    public void Given_TheGeneratedBinder_When_ItsTypesAreRead_Then_TheNodeOptionsAreAmongThem()
    {
        // Act
        var types = GetBoundTypes();

        // Assert: the generator is on (src/Directory.Build.props) and reached the daemon's bindings
        Assert.Contains(types, t => t.FullName == "NLightning.Domain.Node.Options.NodeOptions");
        Assert.Contains(types, t => t.FullName == "NLightning.Infrastructure.Bitcoin.Options.BitcoinOptions");
        Assert.True(types.Count > 30, $"Only {types.Count} bound types found");
    }

    [Fact]
    public void Given_EveryTypeTheGeneratorBinds_When_ItsPropertiesAreRead_Then_NoneIsInitOnly()
    {
        // Arrange
        var types = GetBoundTypes();

        // Act
        var initOnly = types.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                                    .Where(IsInitOnly)
                                                    .Select(property => $"{type.FullName}.{property.Name}"))
                            .Order(StringComparer.Ordinal)
                            .ToList();

        // Assert
        Assert.Empty(initOnly);
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) == true;

    /// <summary>
    /// The <c>ref T instance</c> types of the generated <c>BindCore</c> overloads (the file-local
    /// <c>Microsoft.Extensions.Configuration.Binder.SourceGeneration.BindingExtensions</c> class), without the
    /// collection types.
    /// </summary>
    private static List<Type> GetBoundTypes()
    {
        var binder = typeof(NodeServiceExtensions).Assembly.GetTypes()
                                                  .Single(t => t.Namespace
                                                            == "Microsoft.Extensions.Configuration.Binder.SourceGeneration"
                                                            && t.Name.EndsWith("BindingExtensions", StringComparison.Ordinal));

        return binder.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(m => m.Name == "BindCore")
                     .Select(m => m.GetParameters()[1].ParameterType)
                     .Where(t => t.IsByRef)
                     .Select(t => t.GetElementType()!)
                     .Where(t => !t.IsGenericType && !t.IsArray)
                     .Distinct()
                     .ToList();
    }
}