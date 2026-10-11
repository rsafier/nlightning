using System.Text.RegularExpressions;

namespace NLightning.Integration.Tests.BOLT8;

using Fixtures;

/// <summary>
/// NL-1369: every outbound peer connection is opened by <c>TcpService.ConnectToPeerAsync</c>, which asks a host's
/// <c>IPeerConnectionDialer</c> first, and only <c>PeerManager</c> calls it (startup, reconnect backoff, connect,
/// dial): a new caller elsewhere would dial past the dialer. Reads the sources of the checkout the test assembly was
/// built from (skipped without one).
/// </summary>
public partial class PeerDialPathTests
{
    [Fact]
    public void Given_TheSources_When_Scanned_Then_OnlyThePeerManagerDialsThroughTheTcpService()
    {
        // Arrange
        var root = LnUnitAbsenceTests.FindRepositoryRoot();

        // Act
        var callers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                               .Where(path => File.ReadLines(path).Any(line => TcpServiceDial().IsMatch(line)))
                               .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                               .ToList();

        // Assert
        Assert.Equal(["src/NLightning.Application/Node/Managers/PeerManager.cs"], callers);
    }

    [GeneratedRegex(@"[Tt]cpService\??\.ConnectToPeerAsync\(")]
    private static partial Regex TcpServiceDial();
}