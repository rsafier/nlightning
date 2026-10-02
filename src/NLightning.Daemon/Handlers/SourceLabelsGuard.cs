namespace NLightning.Daemon.Handlers;

using Domain.Accounting.Labels;
using Domain.Client.Constants;
using Domain.Client.Exceptions;

/// <summary>
/// Checks the operator's label and tags of a client request (NL-602 A3-T1: <c>createinvoice</c>, <c>payinvoice</c>,
/// <c>keysend</c>, <c>createoffer</c>, <c>payoffer</c>, <c>withdraw</c>, <c>openchannel</c>) with the shared rules of
/// <see cref="SourceLabelRules"/>, before anything is sent or stored.
/// </summary>
internal static class SourceLabelsGuard
{
    /// <summary>
    /// The checked label and tags (<see cref="SourceLabels.None"/> for none).
    /// </summary>
    /// <exception cref="ClientException">A rule is broken (<see cref="ErrorCodes.InvalidOperation"/>, the message says
    /// which).</exception>
    public static SourceLabels Check(string? label, IReadOnlyList<string>? tags)
    {
        return SourceLabels.TryCreate(label, tags, out var labels, out var error)
                   ? labels
                   : throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid label or tag: {error}");
    }
}