namespace NLightning.Domain.Client.Responses;

using Payments.Models;

/// <summary>
/// The answer to <c>listforwards</c> (<c>ClientCommand.ListForwards</c>, NL-597): a page of forwards, newest first,
/// with the totals over the whole filtered set.
/// </summary>
public sealed class ListForwardsClientResponse
{
    public ListForwardsClientResponse(IReadOnlyList<ForwardInfoClientResponse> forwards,
                                      ForwardSummaryClientResponse summary)
    {
        Forwards = forwards;
        Summary = summary;
    }

    /// <summary>The page of forwards, newest first.</summary>
    public IReadOnlyList<ForwardInfoClientResponse> Forwards { get; }

    /// <summary>The counts and fees over the whole filtered set, with the refused-HTLC counters (NL-598).</summary>
    public ForwardSummaryClientResponse Summary { get; }

    /// <summary>Maps the models of one page (the summary is built by the handler).</summary>
    public static ListForwardsClientResponse FromModels(IReadOnlyList<ForwardCircuitModel> circuits,
                                                        ForwardSummaryClientResponse summary) =>
        new(circuits.Select(c => ForwardInfoClientResponse.FromModel(c)).ToList(), summary);
}