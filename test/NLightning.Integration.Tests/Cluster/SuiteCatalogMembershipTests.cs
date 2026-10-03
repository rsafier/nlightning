using System.Reflection;

namespace NLightning.Integration.Tests.Cluster;

using Docker.Gossip;
using Docker.Interop.Cln;
using Docker.Interop.Eclair;
using Docker.Interop.Ldk;
using Docker.Interop.Tor;
using Docker.Onchain;
using Docker.Taproot;
using TestCollections;
using Testing.Cluster.Run.Matrix;

/// <summary>
/// Guards <c>scripts/run-cluster.sh</c>'s suite catalog (<see cref="SuiteCatalog"/>, test harness phase 5) against this
/// assembly: every container-backed test class runs in exactly one suite, and each suite holds only classes of the
/// fixture collection it stands for (one fixture, so one set of namespaces, per suite process). A new class in the
/// container namespaces fails here until the catalog places it.
/// </summary>
public class SuiteCatalogMembershipTests
{
    private const string ContainerNamespace = "NLightning.Integration.Tests.Docker";

    /// <summary>The fixture collection of each suite's classes (a class without a collection needs no fixture).</summary>
    private static readonly Dictionary<string, string> s_suiteCollections = new(StringComparer.Ordinal)
    {
        ["lnd"] = LightningRegtestNetworkFixtureCollection.Name,
        ["abcd"] = LightningRegtestNetworkFixtureCollection.Name,
        ["onchain"] = OnchainRegtestCollection.Name,
        ["anchors"] = OnchainRegtestCollection.Name,
        ["gossip"] = GossipRegtestCollection.Name,
        ["day0"] = GossipRegtestCollection.Name,
        ["cln"] = ClnInteropCollection.Name,
        ["eclair"] = EclairInteropCollection.Name,
        ["eclair2"] = EclairInteropCollection.Name,
        ["ldk"] = LdkInteropCollection.Name,
        ["tor"] = TorInteropCollection.Name,
        ["taproot"] = LndTaprootRegtestCollection.Name,
        ["postgres"] = "postgres"
    };

    [Fact]
    public void Given_EveryContainerTestClass_When_MatchedAgainstTheCatalog_Then_ItRunsInExactlyOneSuite()
    {
        // Arrange
        var problems = new List<string>();

        // Act
        foreach (var type in ContainerTestClasses())
        {
            var methods = TestMethods(type);
            var suites = SuiteCatalog.All.Where(s => methods.Any(m => RunsIn(s, type, m))).Select(s => s.Name).ToList();
            if (suites.Count > 1)
                problems.Add($"{type.FullName} runs in {string.Join(", ", suites)}");

            // Explicit-only classes (captures) and SQL Server classes (never ported, owner decision) run nowhere
            if (suites.Count == 0 && !methods.All(m => IsExplicit(m) || IsSqlServer(type, m)))
                problems.Add($"{type.FullName} runs in no suite");
        }

        // Assert
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Given_EverySuite_When_ItsClassesAreListed_Then_TheyShareTheSuitesFixtureCollection()
    {
        // Arrange
        var problems = new List<string>();

        // Act
        foreach (var suite in SuiteCatalog.All.Where(s => s.Name != "faults"))
        {
            var classes = ContainerTestClasses().Where(t => TestMethods(t).Any(m => Matches(suite, t, m))).ToList();
            if (classes.Count == 0)
                problems.Add($"{suite.Name} selects no class");

            foreach (var type in classes)
            {
                var collection = CollectionOf(type);
                if (collection is not null && collection != s_suiteCollections[suite.Name])
                    problems.Add($"{suite.Name}: {type.FullName} is in collection '{collection}'");
            }
        }

        // Assert
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static IEnumerable<Type> ContainerTestClasses() =>
        typeof(SuiteCatalogMembershipTests).Assembly.GetTypes()
                                           .Where(t => t is { IsClass: true, IsAbstract: false }
                                                    && (t.Namespace ?? "").StartsWith(ContainerNamespace,
                                                                                       StringComparison.Ordinal)
                                                    && TestMethods(t).Count > 0)
                                           .OrderBy(t => t.FullName, StringComparer.Ordinal);

    private static List<MethodInfo> TestMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .ToList();

    private static bool IsExplicit(MethodInfo method) => method.GetCustomAttribute<FactAttribute>()!.Explicit;

    private static bool IsSqlServer(Type type, MethodInfo method) =>
        TraitsOf(type, method).Any(t => t is { Key: "Database", Value: "SqlServer" });

    private static bool Matches(MatrixSuite suite, Type type, MethodInfo method) =>
        SuiteFilter.Matches(suite.Selection.Concat(suite.Constraints).Concat(SuiteCatalog.GlobalConstraints).ToList(),
                            type.FullName!, method.Name, TraitsOf(type, method));

    /// <summary>The suite selects the test and its explicit mode runs it.</summary>
    private static bool RunsIn(MatrixSuite suite, Type type, MethodInfo method) =>
        Matches(suite, type, method)
     && suite.Explicit switch
     {
         "off" => !IsExplicit(method),
         "only" => IsExplicit(method),
         _ => true
     };

    private static List<KeyValuePair<string, string>> TraitsOf(Type type, MethodInfo method) =>
        type.GetCustomAttributesData()
            .Concat(method.GetCustomAttributesData())
            .Where(a => a.AttributeType == typeof(TraitAttribute))
            .Select(a => new KeyValuePair<string, string>((string)a.ConstructorArguments[0].Value!,
                                                          (string)a.ConstructorArguments[1].Value!))
            .ToList();

    private static string? CollectionOf(Type type) =>
        type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(CollectionAttribute))
            .Select(a => a.ConstructorArguments[0].Value as string)
            .FirstOrDefault();
}