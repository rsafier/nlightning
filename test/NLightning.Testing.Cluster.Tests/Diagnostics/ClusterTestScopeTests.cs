namespace NLightning.Testing.Cluster.Tests.Diagnostics;

using Cluster.Diagnostics;

public class ClusterTestScopeTests
{
    [Fact]
    public void Given_ATestBody_When_TheScopeIsRead_Then_ItNamesTheTestAndItsCollection()
    {
        // Act
        var scope = ClusterTestScope.Current();

        // Assert
        Assert.Equal(TestContext.Current.Test!.UniqueID, scope.TestId);
        Assert.Equal(TestContext.Current.TestCollection!.UniqueID, scope.CollectionId);
        Assert.Equal(
            "ClusterTestScopeTests.Given_ATestBody_When_TheScopeIsRead_Then_ItNamesTheTestAndItsCollection",
            scope.Label);
    }

    [Theory]
    [InlineData("Class.Method", "Class.Method")]
    [InlineData("a b/c:d", "a_b_c_d")]
    [InlineData("...", "test")]
    public void Given_PlainText_When_MadeALabel_Then_UnsafeCharactersAreReplaced(string text, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, ClusterTestScope.ToLabel(text));
    }

    [Fact]
    public void Given_TheoryRows_When_MadeLabels_Then_EachRowGetsItsOwnHashedLabel()
    {
        // Act
        var first = ClusterTestScope.ToLabel("Tests.Method(version: \"29.0\")");
        var second = ClusterTestScope.ToLabel("Tests.Method(version: \"31.1\")");

        // Assert
        Assert.StartsWith("Tests.Method-", first);
        Assert.NotEqual(first, second);
        Assert.Equal(first, ClusterTestScope.ToLabel("Tests.Method(version: \"29.0\")"));
    }

    [Fact]
    public void Given_AVeryLongName_When_MadeALabel_Then_ItIsCutWithAHash()
    {
        // Act
        var label = ClusterTestScope.ToLabel(new string('x', 300));

        // Assert
        Assert.Equal(ClusterTestScope.MaxLabelLength, label.Length);
    }

    [Fact]
    public void Given_RunsOfDifferentScopes_When_ATestFails_Then_ItCoversItsOwnAndItsFixturesOnly()
    {
        // Arrange
        var failing = new ClusterTestScope("t1", "A.M", "c1", "C");
        var ownRun = new ClusterTestScope("t1", "A.M", "c1", "C");
        var otherTest = new ClusterTestScope("t2", "A.N", "c1", "C");
        var fixtureOfCollection = new ClusterTestScope(null, null, "c1", "C");
        var fixtureOfOtherCollection = new ClusterTestScope(null, null, "c2", "D");
        var outsideXunit = ClusterTestScope.None;

        // Act & Assert
        Assert.True(failing.Covers(ownRun));
        Assert.False(failing.Covers(otherTest));
        Assert.True(failing.Covers(fixtureOfCollection));
        Assert.False(failing.Covers(fixtureOfOtherCollection));
        Assert.True(failing.Covers(outsideXunit));
    }

    [Fact]
    public void Given_ScopesWithoutATest_When_Labelled_Then_FixturesAndRunsAreNamed()
    {
        // Act & Assert
        Assert.Equal("fixture-ClnCollection", new ClusterTestScope(null, null, "c", "ClnCollection").Label);
        Assert.Equal("run", ClusterTestScope.None.Label);
    }
}