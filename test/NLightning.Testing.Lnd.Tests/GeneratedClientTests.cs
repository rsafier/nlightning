using System.Reflection;
using Google.Protobuf.Reflection;
using Grpc.Core;

namespace NLightning.Testing.Lnd.Tests;

using TestUtils;

/// <summary>Every LND service of the fetched protos has a generated client, reachable from <see cref="LndNodeConnection"/>.</summary>
public class GeneratedClientTests
{
    private static readonly Assembly s_assembly = typeof(LndNodeConnection).Assembly;

    /// <summary>(C# namespace, service class, LND's full service name, LndNodeConnection property).</summary>
    public static TheoryData<string, string, string, string> Services => new()
    {
        { "NLightning.Testing.Lnd.Lnrpc", "Lightning", "lnrpc.Lightning", nameof(LndNodeConnection.LightningClient) },
        { "NLightning.Testing.Lnd.Lnrpc", "State", "lnrpc.State", nameof(LndNodeConnection.StateClient) },
        {
            "NLightning.Testing.Lnd.Lnrpc", "WalletUnlocker", "lnrpc.WalletUnlocker",
            nameof(LndNodeConnection.WalletUnlockerClient)
        },
        { "NLightning.Testing.Lnd.Routerrpc", "Router", "routerrpc.Router", nameof(LndNodeConnection.RouterClient) },
        { "NLightning.Testing.Lnd.Signrpc", "Signer", "signrpc.Signer", nameof(LndNodeConnection.SignClient) },
        {
            "NLightning.Testing.Lnd.Walletrpc", "WalletKit", "walletrpc.WalletKit",
            nameof(LndNodeConnection.WalletKitClient)
        },
        {
            "NLightning.Testing.Lnd.Invoicesrpc", "Invoices", "invoicesrpc.Invoices",
            nameof(LndNodeConnection.InvoiceClient)
        },
        {
            "NLightning.Testing.Lnd.Chainrpc", "ChainNotifier", "chainrpc.ChainNotifier",
            nameof(LndNodeConnection.ChainNotifierClient)
        },
        { "NLightning.Testing.Lnd.Chainrpc", "ChainKit", "chainrpc.ChainKit", nameof(LndNodeConnection.ChainKitClient) },
        { "NLightning.Testing.Lnd.Peersrpc", "Peers", "peersrpc.Peers", nameof(LndNodeConnection.PeersClient) },
        { "NLightning.Testing.Lnd.Devrpc", "Dev", "devrpc.Dev", nameof(LndNodeConnection.DevClient) },
        {
            "NLightning.Testing.Lnd.Verrpc", "Versioner", "verrpc.Versioner",
            nameof(LndNodeConnection.VersionerClient)
        },
        {
            "NLightning.Testing.Lnd.Autopilotrpc", "Autopilot", "autopilotrpc.Autopilot",
            nameof(LndNodeConnection.AutopilotClient)
        },
        {
            "NLightning.Testing.Lnd.Watchtowerrpc", "Watchtower", "watchtowerrpc.Watchtower",
            nameof(LndNodeConnection.WatchtowerClient)
        },
        {
            "NLightning.Testing.Lnd.Wtclientrpc", "WatchtowerClient", "wtclientrpc.WatchtowerClient",
            nameof(LndNodeConnection.WtClientClient)
        },
        {
            "NLightning.Testing.Lnd.Neutrinorpc", "NeutrinoKit", "neutrinorpc.NeutrinoKit",
            nameof(LndNodeConnection.NeutrinoKitClient)
        }
    };

    [Theory]
    [MemberData(nameof(Services))]
    public void Given_AService_When_LookingUpItsClient_Then_AGeneratedClientExistsWithLndsWireName(
        string csharpNamespace, string service, string fullName, string property)
    {
        // Arrange
        var serviceType = s_assembly.GetType($"{csharpNamespace}.{service}");

        // Act
        var clientType = serviceType?.GetNestedType($"{service}Client");
        var descriptor = (ServiceDescriptor?)serviceType?.GetProperty("Descriptor")?.GetValue(null);

        // Assert
        Assert.NotNull(clientType);
        Assert.True(typeof(ClientBase).IsAssignableFrom(clientType));
        Assert.NotNull(clientType.GetConstructor([typeof(ChannelBase)]));
        Assert.Equal(fullName, descriptor?.FullName);
        Assert.Equal(clientType, typeof(LndNodeConnection).GetProperty(property)?.PropertyType);
    }

    [Fact]
    public void Given_TheGeneratedTypes_When_ListingTheirNamespaces_Then_AllAreUnderNLightningTestingLnd()
    {
        // Act
        var outside = s_assembly.GetTypes()
                                .Where(x => x.Namespace is null
                                         || !x.Namespace.StartsWith("NLightning.Testing.Lnd", StringComparison.Ordinal))
                                .Where(x => x.FullName?.StartsWith('<') != true)
                                .Select(x => x.FullName)
                                .ToList();

        // Assert
        Assert.Empty(outside);
    }

    [Fact]
    public void Given_AConnection_When_Created_Then_EveryClientPropertyIsSetWithoutCallingTheNode()
    {
        // Arrange
        using var certificate = TestCertificates.CreateServerCertificate();
        var settings = LndSettings.FromBytes("https://127.0.0.1:1", TestCertificates.ToPem(certificate), [0x02]);

        // Act
        using var connection = LndNodeConnection.CreateWithoutNodeInfo(settings);
        var clientProperties = typeof(LndNodeConnection).GetProperties()
                                                        .Where(x => typeof(ClientBase).IsAssignableFrom(x.PropertyType))
                                                        .ToList();

        // Assert
        Assert.Equal(Services.Count, clientProperties.Count);
        Assert.All(clientProperties, x => Assert.NotNull(x.GetValue(connection)));
        Assert.Equal(string.Empty, connection.LocalNodePubKey);
    }
}