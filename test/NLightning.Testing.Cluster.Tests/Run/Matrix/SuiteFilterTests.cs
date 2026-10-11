namespace NLightning.Testing.Cluster.Tests.Run.Matrix;

using Cluster.Run.Matrix;

public class SuiteFilterTests
{
    private static readonly KeyValuePair<string, string>[] s_none = [];

    [Theory]
    [InlineData("Ns.A", "Ns.A", true)]
    [InlineData("Ns.A", "Ns.AB", false)]
    [InlineData("Ns.Onchain.OnchainO2Tests", "Ns.Onchain.Onchain*", true)]
    [InlineData("Ns.Onchain.Anchors.AnchorsO2Tests", "Ns.Onchain.Onchain*", false)]
    [InlineData("Ns.Gossip.Capture.X", "*Capture*", true)]
    [InlineData("Ns.X", "*.X", true)]
    [InlineData("anything", "*", true)]
    public void Given_APattern_When_Compared_Then_StarsMatchOnlyAtTheEnds(string text, string pattern, bool expected)
    {
        // Act & Assert
        Assert.Equal(expected, SuiteFilter.Like(text, pattern));
    }

    [Fact]
    public void Given_FiltersOfOneKind_When_Matched_Then_TheyAreOredAndKindsAreAnded()
    {
        // Arrange
        string[] filters = ["-class", "Ns.A", "-class", "Ns.B", "-trait", "Category=X"];
        KeyValuePair<string, string>[] x = [new("Category", "X")];

        // Act & Assert
        Assert.True(SuiteFilter.Matches(filters, "Ns.A", "M", x));
        Assert.True(SuiteFilter.Matches(filters, "Ns.B", "M", x));
        Assert.False(SuiteFilter.Matches(filters, "Ns.C", "M", x));
        Assert.False(SuiteFilter.Matches(filters, "Ns.A", "M", s_none));
    }

    [Fact]
    public void Given_NegativeFilters_When_Matched_Then_AnyOfThemExcludes()
    {
        // Arrange
        string[] filters =
            ["-namespace", "Ns", "-class-", "Ns.Skip", "-trait-", "Database=SqlServer", "-method-", "Ns.A.Slow"];

        // Act & Assert
        Assert.True(SuiteFilter.Matches(filters, "Ns.A", "Fast", s_none));
        Assert.False(SuiteFilter.Matches(filters, "Ns.Skip", "Fast", s_none));
        Assert.False(SuiteFilter.Matches(filters, "Ns.A", "Slow", s_none));
        Assert.False(SuiteFilter.Matches(filters, "Ns.A", "Fast", [new("Database", "SqlServer")]));
        Assert.False(SuiteFilter.Matches(filters, "Ns.Sub.A", "Fast", s_none)); // -namespace is exact
    }

    [Fact]
    public void Given_NoPositiveFilter_When_Matched_Then_EverythingNotExcludedMatches()
    {
        // Act & Assert
        Assert.True(SuiteFilter.Matches(["-trait-", "Database=SqlServer"], "Any.Class", "M", s_none));
        Assert.True(SuiteFilter.Matches([], "Any.Class", "M", s_none));
    }

    [Theory]
    [InlineData("-class")]
    [InlineData("-bogus", "x")]
    [InlineData("-trait", "novalue")]
    public void Given_ABadFilter_When_Matched_Then_ItIsRefused(params string[] filters)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => SuiteFilter.Matches(filters, "Ns.A", "M", s_none));
    }
}