namespace NLightning.Client.Handlers;

using Domain.Accounting.Labels;

/// <summary>
/// The operator's label and tags of a command (NL-602 A3-T1), checked with the daemon's rules
/// (<see cref="SourceLabelRules"/>); <see cref="Tags"/> are <c>key=value</c>, sorted by key.
/// </summary>
public sealed record LabelArguments(string? Label, IReadOnlyList<string> Tags)
{
    /// <summary>No label and no tags.</summary>
    public static LabelArguments None { get; } = new(null, []);

    /// <summary>The IPC form of <see cref="Tags"/>: null for none.</summary>
    public List<string>? TagsOrNull => Tags.Count == 0 ? null : [.. Tags];
}

/// <summary>
/// <c>--label &lt;text&gt;</c> and repeatable <c>--tag &lt;key&gt;=&lt;value&gt;</c> (each also as
/// <c>--option=value</c>, anywhere after the command) on <c>createinvoice</c>, <c>payinvoice</c>, <c>keysend</c>,
/// <c>payroute</c>, <c>createoffer</c>, <c>payoffer</c>, <c>withdraw</c> and <c>openchannel</c> (NL-602 A3-T1, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §9). They are taken out of the arguments before the command's own parser
/// runs, so every command takes them the same way.
/// </summary>
internal static class LabelOptions
{
    /// <summary>The options' usage, appended to each labelled command's.</summary>
    internal const string Usage = "[--label <text>] [--tag <key>=<value>]...";

    private const string LabelOption = "--label";
    private const string TagOption = "--tag";

    /// <summary>
    /// Whether <paramref name="cmd"/> (any of its aliases) takes <c>--label</c> and <c>--tag</c>.
    /// </summary>
    internal static bool IsLabelledCommand(string cmd) =>
        cmd is "createinvoice" or "create-invoice" or "addinvoice"
            or "payinvoice" or "pay-invoice" or "pay"
            or "keysend"
            or "payroute" or "pay-route"
            or "createoffer" or "create-offer"
            or "payoffer" or "pay-offer"
            or "withdraw" or "send-coins" or "sendcoins"
            or "openchannel" or "open-channel";

    /// <summary>
    /// Takes <c>--label</c> and <c>--tag</c> out of <paramref name="commandArgs"/> and checks them.
    /// </summary>
    /// <returns>The other arguments, in order, or null with <paramref name="error"/> set (and
    /// <paramref name="labels"/> <see cref="LabelArguments.None"/>).</returns>
    internal static string[]? Extract(string[] commandArgs, out LabelArguments labels, out string? error)
    {
        labels = LabelArguments.None;
        error = null;
        string? label = null;
        var labelGiven = false;
        var tags = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            var separator = argument.IndexOf('=');
            var name = separator < 0 ? argument : argument[..separator];
            var isLabel = string.Equals(name, LabelOption, StringComparison.OrdinalIgnoreCase);
            if (!isLabel && !string.Equals(name, TagOption, StringComparison.OrdinalIgnoreCase))
            {
                rest.Add(argument);
                continue;
            }

            string value;
            if (separator >= 0)
            {
                value = argument[(separator + 1)..];
            }
            else if (i + 1 < commandArgs.Length)
            {
                value = commandArgs[++i];
            }
            else
            {
                error = $"Missing value for {name.ToLowerInvariant()}.";
                return null;
            }

            if (isLabel)
            {
                if (labelGiven)
                {
                    error = "--label is given more than once.";
                    return null;
                }

                labelGiven = true;
                label = value;
            }
            else
            {
                tags.Add(value);
            }
        }

        if (!SourceLabels.TryCreate(label, tags, out var checkedLabels, out var labelError))
        {
            error = $"Invalid label or tag: {labelError}";
            return null;
        }

        labels = checkedLabels.IsEmpty
                     ? LabelArguments.None
                     : new LabelArguments(checkedLabels.Label, checkedLabels.TagStrings);
        return rest.ToArray();
    }

    /// <summary>
    /// The label and tags for printing: <c>label</c>, then <c>[k=v, ...]</c>; empty when there are none.
    /// </summary>
    internal static string Format(string? label, IReadOnlyList<string>? tags)
    {
        var hasTags = tags is { Count: > 0 };
        if (label is null && !hasTags)
            return string.Empty;

        return hasTags
                   ? $"{label}{(label is null ? "" : " ")}[{string.Join(", ", tags!)}]"
                   : label!;
    }
}