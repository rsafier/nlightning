namespace NLightning.Application.Tests.Node.Managers;

/// <summary>
/// Every <see cref="PeerManagerBatchTests"/> case with per-channel lanes on and the batch channels registered: the
/// dispatcher groups <c>start_batch</c> as the plain loop does and a complete batch is one item of its channel's lane.
/// </summary>
public class PeerManagerBatchLanedTests : PeerManagerBatchTests
{
    protected override bool UseChannelLanes => true;
}