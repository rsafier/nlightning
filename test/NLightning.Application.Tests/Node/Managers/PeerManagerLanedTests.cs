namespace NLightning.Application.Tests.Node.Managers;

/// <summary>
/// Every <see cref="PeerManagerTests"/> case with per-channel lanes on (<c>Node:ChannelLanes:Enabled</c>): the peer lane
/// keeps the plain loop's rules.
/// </summary>
[Collection("timing-serial")]
public class PeerManagerLanedTests : PeerManagerTests
{
    protected override bool UseChannelLanes => true;
}