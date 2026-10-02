using Google.Protobuf.Reflection;

namespace NLightning.Testing.Lnd.Tests;

/// <summary>
/// lnunit.lnd 3.0.4 and this client in one assembly, as during phase 3 of the test harness plan: the types do not
/// clash, and the only API lnunit.lnd's protos (LND 0.20) have that ours (LND 0.21.4) lack is what LND 0.21 removed.
/// Remove this class with the LNUnit.LND package reference once the tests no longer use LNUnit.
/// </summary>
public class LnUnitCoexistenceTests
{
    /// <summary>The deprecated RPCs, messages and fields LND 0.21 dropped; none is used by the Docker tests.</summary>
    private static readonly string[] s_removedInLnd021 =
    [
        "enum routerrpc.PaymentState",
        "field lnrpc.QueryRoutesRequest.outgoing_chan_id",
        "field routerrpc.SendPaymentRequest.outgoing_chan_id",
        "message lnrpc.SendRequest",
        "message lnrpc.SendRequest.DestCustomRecordsEntry",
        "message lnrpc.SendResponse",
        "message lnrpc.SendToRouteRequest",
        "message routerrpc.PaymentStatus",
        "message routerrpc.SendToRouteResponse",
        "method lnrpc.Lightning.SendPayment",
        "method lnrpc.Lightning.SendPaymentSync",
        "method lnrpc.Lightning.SendToRoute",
        "method lnrpc.Lightning.SendToRouteSync",
        "method routerrpc.Router.SendPayment",
        "method routerrpc.Router.SendToRoute",
        "method routerrpc.Router.TrackPayment"
    ];

    private static readonly FileDescriptor[] s_lnUnit =
    [
        global::Lnrpc.LightningReflection.Descriptor, global::Lnrpc.StateserviceReflection.Descriptor,
        global::Routerrpc.RouterReflection.Descriptor, global::Signrpc.SignerReflection.Descriptor,
        global::Walletrpc.WalletkitReflection.Descriptor, global::Invoicesrpc.InvoicesReflection.Descriptor,
        global::Chainrpc.ChainnotifierReflection.Descriptor, global::Peersrpc.PeersReflection.Descriptor,
        global::Devrpc.DevReflection.Descriptor, global::Verrpc.VerrpcReflection.Descriptor,
        global::Autopilotrpc.AutopilotReflection.Descriptor, global::Watchtowerrpc.WatchtowerReflection.Descriptor,
        global::Wtclientrpc.WtclientReflection.Descriptor, global::Neutrinorpc.NeutrinoReflection.Descriptor
    ];

    private static readonly FileDescriptor[] s_ours =
    [
        Lnrpc.LightningReflection.Descriptor, Lnrpc.StateserviceReflection.Descriptor,
        Lnrpc.WalletunlockerReflection.Descriptor, Routerrpc.RouterReflection.Descriptor,
        Signrpc.SignerReflection.Descriptor, Walletrpc.WalletkitReflection.Descriptor,
        Invoicesrpc.InvoicesReflection.Descriptor, Chainrpc.ChainnotifierReflection.Descriptor,
        Chainrpc.ChainkitReflection.Descriptor, Peersrpc.PeersReflection.Descriptor, Devrpc.DevReflection.Descriptor,
        Verrpc.VerrpcReflection.Descriptor, Autopilotrpc.AutopilotReflection.Descriptor,
        Watchtowerrpc.WatchtowerReflection.Descriptor, Wtclientrpc.WtclientReflection.Descriptor,
        Neutrinorpc.NeutrinoReflection.Descriptor
    ];

    [Fact]
    public void Given_BothClientSets_When_UsedInOneAssembly_Then_TheirTypesAreDistinctAndShareWireNames()
    {
        // Act
        var theirs = typeof(global::Lnrpc.Lightning.LightningClient);
        var ours = typeof(Lnrpc.Lightning.LightningClient);

        // Assert
        Assert.NotEqual(theirs, ours);
        Assert.Equal("Lnrpc", theirs.Namespace);
        Assert.Equal("NLightning.Testing.Lnd.Lnrpc", ours.Namespace);
        Assert.Equal(global::Lnrpc.Lightning.Descriptor.FullName, Lnrpc.Lightning.Descriptor.FullName);
    }

    [Fact]
    public void Given_LnUnitsProtos_When_ComparedWithOurs_Then_OnlyWhatLnd021RemovedIsMissing()
    {
        // Arrange
        var ours = Index(s_ours);

        // Act
        var missing = Index(s_lnUnit).Where(x => !ours.TryGetValue(x.Key, out var shape) || shape != x.Value)
                                     .Select(x => x.Key)
                                     .Where(x => Parent(x) is not { } parent || ours.ContainsKey(parent))
                                     .Order(StringComparer.Ordinal);

        // Assert
        Assert.Equal(s_removedInLnd021, missing);
    }

    /// <summary>The message of a field or the enum of a value: a member of a removed type is not listed on its own.</summary>
    private static string? Parent(string key)
    {
        var kind = key[..key.IndexOf(' ')];
        var name = key[(kind.Length + 1)..];
        return kind switch
        {
            "field" => $"message {name[..name.LastIndexOf('.')]}",
            "enumvalue" => $"enum {name[..name.LastIndexOf('.')]}",
            _ => null
        };
    }

    /// <summary>Every method, message, field, enum and enum value, keyed by kind and full name, with its shape.</summary>
    private static Dictionary<string, string> Index(IEnumerable<FileDescriptor> files)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var service in file.Services)
                foreach (var method in service.Methods)
                    index[$"method {method.FullName}"] =
                        $"{method.InputType.FullName}>{method.OutputType.FullName} {method.IsClientStreaming}/{method.IsServerStreaming}";
            AddEnums(file.EnumTypes);
            AddMessages(file.MessageTypes);
        }

        return index;

        void AddMessages(IEnumerable<MessageDescriptor> messages)
        {
            foreach (var message in messages)
            {
                index[$"message {message.FullName}"] = string.Empty;
                foreach (var field in message.Fields.InDeclarationOrder())
                    index[$"field {message.FullName}.{field.Name}"] =
                        $"{field.FieldNumber} {field.FieldType} {field.IsRepeated} {field.PropertyName}";
                AddEnums(message.EnumTypes);
                AddMessages(message.NestedTypes);
            }
        }

        void AddEnums(IEnumerable<EnumDescriptor> enums)
        {
            foreach (var type in enums)
            {
                index[$"enum {type.FullName}"] = string.Empty;
                foreach (var value in type.Values)
                    index[$"enumvalue {type.FullName}.{value.Name}"] = value.Number.ToString();
            }
        }
    }
}