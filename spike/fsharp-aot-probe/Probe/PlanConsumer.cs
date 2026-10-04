using FsProbe.Reestablish;

static class PlanConsumer
{
    // What C# sees: Plan.Resume/Fail/DataLoss subclasses, Tag, IsResume..., Item properties named after the fields.
    // C# switch over an F# DU is not exhaustiveness-checked: the discard arm is required (CS8509 otherwise).
    public static string Describe(Plan plan) => plan switch
    {
        Plan.Resume r => $"resume {r.steps.Length} {(r.peerSpliceLocked.IsSome ? "locked" : "")}",
        Plan.Fail f => $"fail [{f.requirementId}] {f.reason} {f.mustBroadcast}",
        Plan.DataLoss d => $"data loss {d.reason}",

    };

    public static Plan Make() => Plan.NewFail("B2-RE-19", "x", Microsoft.FSharp.Collections.FSharpList<Step>.Empty, false);
    public static bool IsTx(Step s) => s.IsTxAbort || s == Step.ChannelReady;
}
