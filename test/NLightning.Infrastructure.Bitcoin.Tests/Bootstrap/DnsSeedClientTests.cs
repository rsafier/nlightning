using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Bootstrap;

using Bitcoin.Bootstrap;
using Domain.Crypto.ValueObjects;
using Domain.Node.Bootstrap;
using Domain.Node.Options;
using Infrastructure.Protocol.Dns;

public class DnsSeedClientTests
{
    private const string Root = "nodes.lightning.directory";

    private readonly Mock<IDnsRecordLookup> _lookup = new(MockBehavior.Strict);
    private readonly NodeOptions _nodeOptions = new();

    private DnsSeedClient CreateClient() =>
        new(_lookup.Object, Microsoft.Extensions.Options.Options.Create(_nodeOptions), NullLogger<DnsSeedClient>.Instance);

    private static (CompactPubKey Key, string Target) NewNode(string root = Root)
    {
        var key = new CompactPubKey(new Key().PubKey.ToBytes());
        return (key, $"{LightningNodeIdBech32.Encode(key)}.{root}");
    }

    private void SetupQuery(string name, DnsRecordKind kind, DnsLookupResponse response) =>
        _lookup.Setup(l => l.QueryAsync(name, kind, It.IsAny<CancellationToken>())).ReturnsAsync(response);

    private static DnsLookupResponse Srv(IEnumerable<DnsSrv> records, IEnumerable<DnsGlue>? glue = null) =>
        new(DnsLookupStatus.NoError, records.ToList(), [], glue?.ToList() ?? []);

    private static DnsLookupResponse Addresses(params string[] addresses) =>
        new(DnsLookupStatus.NoError, [], addresses.Select(IPAddress.Parse).ToList(), []);

    [Fact]
    public async Task Given_ThreeSrvTargetsWithAAnswers_When_Queried_Then_CandidatesCarryTheSrvPortsAndDecodedIds()
    {
        // Arrange
        var nodes = new[] { NewNode(), NewNode(), NewNode() };
        ushort[] ports = [9735, 9766, 8739];
        SetupQuery(Root, DnsRecordKind.Srv, Srv(nodes.Select((n, i) => new DnsSrv(10, 10, ports[i], n.Target + "."))));
        for (var i = 0; i < nodes.Length; i++)
            SetupQuery(nodes[i].Target, DnsRecordKind.A, Addresses($"1.2.3.{i + 1}"));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DnsSeedOutcome.Ok, result.Outcome);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(3, result.Candidates.Count);
        for (var i = 0; i < nodes.Length; i++)
        {
            var candidate = Assert.Single(result.Candidates, c => c.NodeId == nodes[i].Key);
            Assert.Equal(ports[i], candidate.Port);
            Assert.Equal(IPAddress.Parse($"1.2.3.{i + 1}"), candidate.Address);
            Assert.Equal(Root, candidate.Seed);
        }
    }

    [Fact]
    public async Task Given_GlueInTheAdditionalSection_When_Queried_Then_ItIsUsedWithoutAnAQuery()
    {
        // Arrange
        var node = NewNode();
        SetupQuery(Root, DnsRecordKind.Srv,
                   Srv([new DnsSrv(10, 10, 9735, node.Target)],
                       [new DnsGlue(node.Target.ToUpperInvariant() + ".", IPAddress.Parse("8.8.4.4"))]));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(IPAddress.Parse("8.8.4.4"), candidate.Address);
        _lookup.Verify(l => l.QueryAsync(It.IsAny<string>(), DnsRecordKind.A, It.IsAny<CancellationToken>()),
                       Times.Never);
    }

    [Fact]
    public async Task Given_BothFamilies_When_Queried_Then_EveryAAndAaaaAddressIsKept()
    {
        // Arrange
        var node = NewNode();
        SetupQuery(Root, DnsRecordKind.Srv, Srv([new DnsSrv(10, 10, 9735, node.Target)]));
        SetupQuery(node.Target, DnsRecordKind.A, Addresses("1.2.3.4", "5.6.7.8"));
        SetupQuery(node.Target, DnsRecordKind.Aaaa, Addresses("2606:4700::1"));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.Both, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["1.2.3.4", "5.6.7.8", "2606:4700::1"], result.Candidates.Select(c => c.Address.ToString()));
        Assert.All(result.Candidates, c => Assert.Equal(node.Key, c.NodeId));
    }

    [Fact]
    public async Task Given_IPv4Only_When_Queried_Then_OnlyAIsAskedAndAStrayAaaaGlueIsDropped()
    {
        // Arrange
        var node = NewNode();
        SetupQuery(Root, DnsRecordKind.Srv,
                   Srv([new DnsSrv(10, 10, 9735, node.Target)],
                       [
                           new DnsGlue(node.Target, IPAddress.Parse("1.2.3.4")),
                           new DnsGlue(node.Target, IPAddress.Parse("2606:4700::1"))
                       ]));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), candidate.Address);
        _lookup.Verify(l => l.QueryAsync(It.IsAny<string>(), DnsRecordKind.Aaaa, It.IsAny<CancellationToken>()),
                       Times.Never);
    }

    [Fact]
    public async Task Given_IPv4OnlyAndAnAQueryAnsweringIPv6_When_Queried_Then_TheWrongFamilyIsRejected()
    {
        // Arrange
        var node = NewNode();
        SetupQuery(Root, DnsRecordKind.Srv, Srv([new DnsSrv(10, 10, 9735, node.Target)]));
        SetupQuery(node.Target, DnsRecordKind.A, Addresses("2606:4700::1", "1.2.3.4"));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IPAddress.Parse("1.2.3.4"), Assert.Single(result.Candidates).Address);
        Assert.Equal(1, result.Rejected);
    }

    [Fact]
    public async Task Given_ATargetUnderAnotherRoot_When_Queried_Then_ItsFirstLabelIsTakenLiterally()
    {
        // Arrange: test.nodes.lightning.directory has answered targets under the mainnet root
        const string testRoot = "test.nodes.lightning.directory";
        var node = NewNode(Root);
        SetupQuery(testRoot, DnsRecordKind.Srv, Srv([new DnsSrv(10, 10, 19735, node.Target)]));
        SetupQuery(node.Target, DnsRecordKind.A, Addresses("1.2.3.4"));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(testRoot, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(node.Key, candidate.NodeId);
        Assert.Equal(testRoot, candidate.Seed);
    }

    [Fact]
    public async Task Given_AnEmptyRootSrv_When_Queried_Then_TheNodesTcpAliasIsAsked()
    {
        // Arrange
        var node = NewNode();
        SetupQuery(Root, DnsRecordKind.Srv, DnsLookupResponse.Of(DnsLookupStatus.NoError));
        SetupQuery($"_nodes._tcp.{Root}", DnsRecordKind.Srv, Srv([new DnsSrv(10, 10, 9735, node.Target)]));
        SetupQuery(node.Target, DnsRecordKind.A, Addresses("1.2.3.4"));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DnsSeedOutcome.Ok, result.Outcome);
        Assert.Equal(node.Key, Assert.Single(result.Candidates).NodeId);
    }

    [Theory]
    [InlineData(DnsLookupStatus.NoError, DnsSeedOutcome.Empty)]
    [InlineData(DnsLookupStatus.NxDomain, DnsSeedOutcome.NxDomain)]
    [InlineData(DnsLookupStatus.ServFail, DnsSeedOutcome.ServerFailure)]
    [InlineData(DnsLookupStatus.Refused, DnsSeedOutcome.ServerFailure)]
    [InlineData(DnsLookupStatus.Timeout, DnsSeedOutcome.Timeout)]
    [InlineData(DnsLookupStatus.Other, DnsSeedOutcome.Error)]
    public async Task Given_AFailingSeed_When_Queried_Then_TheOutcomeSaysSoWithoutThrowing(DnsLookupStatus status,
        DnsSeedOutcome expected)
    {
        // Arrange
        SetupQuery(Root, DnsRecordKind.Srv, DnsLookupResponse.Of(status));
        SetupQuery($"_nodes._tcp.{Root}", DnsRecordKind.Srv, DnsLookupResponse.Of(DnsLookupStatus.NxDomain));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.Both, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, result.Outcome);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Given_InvalidRecords_When_Queried_Then_TheyAreCountedAsRejectedAndTheRestKept()
    {
        // Arrange
        var good = NewNode();
        var records = new[]
        {
            new DnsSrv(10, 10, 9735, good.Target),
            new DnsSrv(10, 10, 9735, $"notanodeid.{Root}"),
            new DnsSrv(10, 10, 9735, $"lnbc1qfzcdxeg3nun56n5q2a8xrwt3yg88xt029q9s7j4cn3z2rn7argcz90k.{Root}"),
            new DnsSrv(10, 10, 0, NewNode().Target)
        };
        SetupQuery(Root, DnsRecordKind.Srv,
                   Srv(records, records.Select(r => new DnsGlue(r.Target, IPAddress.Parse("1.2.3.4")))
                                       .Append(new DnsGlue(good.Target, IPAddress.Parse("192.168.1.1")))));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert: two bad labels, one private address, one port 0
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(good.Key, candidate.NodeId);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), candidate.Address);
        Assert.Equal(4, result.Rejected);
    }

    [Fact]
    public async Task Given_DuplicateTargets_When_Queried_Then_EachTargetIsResolvedOnce()
    {
        // Arrange
        var node = NewNode();
        SetupQuery(Root, DnsRecordKind.Srv,
                   Srv([
                       new DnsSrv(10, 10, 9735, node.Target),
                       new DnsSrv(20, 10, 9735, node.Target.ToUpperInvariant() + ".")
                   ]));
        SetupQuery(node.Target, DnsRecordKind.A, Addresses("1.2.3.4"));
        SetupQuery(node.Target.ToUpperInvariant(), DnsRecordKind.A, Addresses("1.2.3.4"));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(result.Candidates);
        _lookup.Verify(l => l.QueryAsync(It.IsAny<string>(), DnsRecordKind.A, It.IsAny<CancellationToken>()),
                       Times.Once);
    }

    [Fact]
    public async Task Given_25RecordsAndACapOf5_When_Queried_Then_5CandidatesAndNoMoreAQueries()
    {
        // Arrange
        var nodes = Enumerable.Range(0, 25).Select(_ => NewNode()).ToArray();
        SetupQuery(Root, DnsRecordKind.Srv, Srv(nodes.Select(n => new DnsSrv(10, 10, 9735, n.Target))));
        var aQueries = 0;
        _lookup.Setup(l => l.QueryAsync(It.Is<string>(n => n != Root), DnsRecordKind.A,
                                        It.IsAny<CancellationToken>()))
               .ReturnsAsync(() =>
                {
                    aQueries++;
                    return Addresses($"1.2.3.{aQueries}");
                });
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 5,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5, result.Candidates.Count);
        Assert.Equal(5, aQueries);
    }

    [Fact]
    public async Task Given_ASeedThatHangs_When_Queried_Then_ThePerSeedTimeoutEndsItAsTimeout()
    {
        // Arrange
        _nodeOptions.Bootstrap.PerSeedTimeout = TimeSpan.FromMilliseconds(100);
        _lookup.Setup(l => l.QueryAsync(Root, DnsRecordKind.Srv, It.IsAny<CancellationToken>()))
               .Returns(async (string _, DnsRecordKind _, CancellationToken ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return DnsLookupResponse.Of(DnsLookupStatus.NoError);
                });
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.Both, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DnsSeedOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task Given_TheCallerCancels_When_Queried_Then_TheCancellationPropagates()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        _lookup.Setup(l => l.QueryAsync(Root, DnsRecordKind.Srv, It.IsAny<CancellationToken>()))
               .Returns(async (string _, DnsRecordKind _, CancellationToken ct) =>
                {
                    await cts.CancelAsync();
                    await Task.Delay(Timeout.Infinite, ct);
                    return DnsLookupResponse.Of(DnsLookupStatus.NoError);
                });
        var client = CreateClient();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.QuerySeedAsync(Root, DnsSeedAddressTypes.Both, 25, cts.Token));
    }

    [Fact]
    public async Task Given_QueryConditions_When_TheConditionalQueryIsEmpty_Then_TheBareRootIsAsked()
    {
        // Arrange
        _nodeOptions.Bootstrap.UseQueryConditions = true;
        var node = NewNode();
        SetupQuery($"a2.r0.{Root}", DnsRecordKind.Srv, DnsLookupResponse.Of(DnsLookupStatus.NoError));
        SetupQuery(Root, DnsRecordKind.Srv, Srv([new DnsSrv(10, 10, 9735, node.Target)]));
        SetupQuery(node.Target, DnsRecordKind.A, Addresses("1.2.3.4"));
        var client = CreateClient();

        // Act
        var result = await client.QuerySeedAsync(Root, DnsSeedAddressTypes.IPv4, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(node.Key, Assert.Single(result.Candidates).NodeId);
        _lookup.Verify(l => l.QueryAsync($"a2.r0.{Root}", DnsRecordKind.Srv, It.IsAny<CancellationToken>()),
                       Times.Once);
    }
}