namespace NLightning.Domain.Channels.Enums;

/// <summary>
/// The BOLT 2 protocol a mutual close was agreed with (NL-610; persisted as a byte: never renumber). It decides who
/// paid the closing fee: the funder with <see cref="Legacy"/>, the closer with <see cref="Simple"/>.
/// </summary>
public enum MutualCloseProtocol : byte
{
    /// <summary><c>closing_signed</c> (with or without <c>fee_range</c>): the funder pays the closing fee.</summary>
    Legacy = 1,

    /// <summary><c>option_simple_close</c> (<c>closing_complete</c>/<c>closing_sig</c>): the closer pays it from its own
    /// output.</summary>
    Simple = 2
}