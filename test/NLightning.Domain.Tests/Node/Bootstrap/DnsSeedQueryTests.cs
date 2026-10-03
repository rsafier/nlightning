namespace NLightning.Domain.Tests.Node.Bootstrap;

using Domain.Node.Bootstrap;

public class DnsSeedQueryTests
{
    private const string Root = "nodes.lightning.directory";
    private const string NodeIdBech32 = "ln1qfzcdxeg3nun56n5q2a8xrwt3yg88xt029q9s7j4cn3z2rn7argcz90kezd";

    [Fact]
    public void Given_OnlyTheRoot_When_BuildingTheName_Then_ItIsTheRoot()
    {
        // Arrange
        var query = new DnsSeedQuery(Root);

        // Act
        var name = query.ToHostName();

        // Assert
        Assert.Equal(Root, name);
    }

    [Fact]
    public void Given_RealmZero_When_BuildingTheName_Then_R0PrefixesTheRoot()
    {
        // Arrange
        var query = new DnsSeedQuery(Root, Realm: 0);

        // Act
        var name = query.ToHostName();

        // Assert
        Assert.Equal($"r0.{Root}", name);
    }

    [Theory]
    [InlineData((byte)2, "a2")]
    [InlineData((byte)4, "a4")]
    [InlineData((byte)6, "a6")]
    public void Given_AddressTypes_When_BuildingTheName_Then_TheALabelCarriesTheBits(byte types, string label)
    {
        // Arrange
        var query = new DnsSeedQuery(Root, AddressTypes: types);

        // Act
        var name = query.ToHostName();

        // Assert
        Assert.Equal($"{label}.{Root}", name);
    }

    [Fact]
    public void Given_ACount_When_BuildingTheName_Then_TheNLabelCarriesIt()
    {
        // Arrange
        var query = new DnsSeedQuery(Root, Count: 10);

        // Act
        var name = query.ToHostName();

        // Assert
        Assert.Equal($"n10.{Root}", name);
    }

    [Fact]
    public void Given_EveryCondition_When_BuildingTheName_Then_TheyComeInOrderLNAREachOnce()
    {
        // Arrange
        var withoutNode = new DnsSeedQuery(Root, Realm: 0, AddressTypes: 2, Count: 10);
        var withNode = withoutNode with { NodeIdLabel = NodeIdBech32 };

        // Act
        var name = withoutNode.ToHostName();
        var nodeName = withNode.ToHostName();

        // Assert
        Assert.Equal($"n10.a2.r0.{Root}", name);
        Assert.Equal($"l{NodeIdBech32}.n10.a2.r0.{Root}", nodeName);
    }

    [Fact]
    public void Given_ANodeIdOf62Characters_When_BuildingTheName_Then_The63OctetLabelIsAccepted()
    {
        // Arrange
        var query = new DnsSeedQuery(Root, NodeIdLabel: NodeIdBech32);

        // Act
        var name = query.ToHostName();

        // Assert
        Assert.Equal(63, name.Split('.')[0].Length);
    }

    [Fact]
    public void Given_ANodeIdOf63Characters_When_BuildingTheName_Then_The64OctetLabelThrows()
    {
        // Arrange
        var query = new DnsSeedQuery(Root, NodeIdLabel: NodeIdBech32 + "q");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => query.ToHostName());
    }

    [Fact]
    public void Given_ARootThatMakesTheName254Characters_When_BuildingTheName_Then_ItThrows()
    {
        // Arrange: four 62-character labels and dots make 254 characters with "r0."
        var label = new string('a', 62);
        var root = $"{label}.{label}.{label}.{new string('b', 62)}";
        var query = new DnsSeedQuery(root, Realm: 0);

        // Act & Assert
        Assert.Equal(254, $"r0.{root}".Length);
        Assert.Throws<ArgumentException>(() => query.ToHostName());
    }

    [Fact]
    public void Given_UpperCaseInput_When_BuildingTheNames_Then_TheyAreLowerCased()
    {
        // Arrange
        var query = new DnsSeedQuery("Nodes.Lightning.DIRECTORY", NodeIdLabel: NodeIdBech32.ToUpperInvariant());

        // Act
        var name = query.ToHostName();

        // Assert
        Assert.Equal($"l{NodeIdBech32}.{Root}", name);
    }

    [Fact]
    public void Given_ARoot_When_BuildingTheSrvAlias_Then_ItIsUnderNodesTcp()
    {
        // Act
        var alias = DnsSeedQuery.SrvAlias("Nodes.Lightning.Directory.");

        // Assert
        Assert.Equal($"_nodes._tcp.{Root}", alias);
    }

    [Fact]
    public void Given_ANodeId_When_BuildingTheVirtualHost_Then_ItIsTheBech32LabelUnderTheRoot()
    {
        // Act
        var host = DnsSeedQuery.VirtualHost(NodeIdBech32.ToUpperInvariant(), Root);

        // Assert
        Assert.Equal($"{NodeIdBech32}.{Root}", host);
    }
}