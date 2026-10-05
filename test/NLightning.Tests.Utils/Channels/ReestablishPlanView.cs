namespace NLightning.Tests.Utils.Channels;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Reestablish;

/// <summary>The case of a <see cref="ReestablishPlan"/>, for table-driven assertions.</summary>
public enum ReestablishOutcome
{
    Resume,
    Fail,
    DataLoss
}

/// <summary>
/// A flat view of the <see cref="ReestablishPlan"/> union for the planner tests, which assert the fields the plan
/// had before the C# 15 pilot (NL-1086) made it a union; product code matches on the cases instead.
/// </summary>
public static class ReestablishPlanView
{
    extension(ReestablishPlan plan)
    {
        public ReestablishOutcome Outcome => plan switch
        {
            ReestablishPlan.Resume => ReestablishOutcome.Resume,
            ReestablishPlan.Fail => ReestablishOutcome.Fail,
            ReestablishPlan.DataLoss => ReestablishOutcome.DataLoss
        };

        public IReadOnlyList<ReestablishStep> Steps => plan switch
        {
            ReestablishPlan.Resume resume => resume.Steps,
            ReestablishPlan.Fail fail => fail.Steps,
            ReestablishPlan.DataLoss => []
        };

        public string? RequirementId => plan switch
        {
            ReestablishPlan.Resume => null,
            ReestablishPlan.Fail fail => fail.RequirementId,
            ReestablishPlan.DataLoss => ReestablishPlan.DataLoss.RequirementId
        };

        public string? Reason => plan switch
        {
            ReestablishPlan.Resume => null,
            ReestablishPlan.Fail fail => fail.Reason,
            ReestablishPlan.DataLoss loss => loss.Reason
        };

        public bool MustBroadcast => plan is ReestablishPlan.Fail { MustBroadcast: true };

        public TxId? PeerSpliceLocked => plan is ReestablishPlan.Resume resume ? resume.PeerSpliceLocked : null;
    }
}