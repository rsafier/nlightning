namespace NLightning.Domain.Node.Options;

using Protocol.ValueObjects;

/// <summary>
/// A custom signet the node runs on (for example Mutinynet). Bound from <c>Node:CustomSignet</c> (it is
/// <see cref="NodeOptions.CustomSignet"/>); only valid with <c>Node:Network</c> <c>signet</c>.
/// </summary>
/// <remarks>
/// Every signet shares the signet genesis block, so a custom signet has the Lightning parameters of
/// <see cref="BitcoinNetwork.Signet"/> (<c>chain_hash</c>, <c>tb</c> addresses, <c>tbs</c> invoices). The block
/// challenge is bitcoind's business (<c>signetchallenge</c> in its bitcoin.conf); the node only needs the name, which
/// <see cref="Register"/> makes resolvable (<see cref="BitcoinNetwork.Resolve"/> maps it to signet).
/// </remarks>
public class CustomSignetOptions
{
    /// <summary>
    /// The custom signet's name, e.g. <c>mutinynet</c>. It names the default config directory
    /// (<c>~/.nltg/&lt;name&gt;</c>) and is accepted by <c>--network</c>.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Registers <see cref="Name"/> as a custom signet (idempotent). Does nothing without a name.
    /// </summary>
    public void Register()
    {
        if (!string.IsNullOrWhiteSpace(Name))
            BitcoinNetwork.RegisterCustomSignet(Name);
    }

    /// <summary>
    /// Returns every configuration error for a node on <paramref name="network"/>; empty when valid.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors(BitcoinNetwork network)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            return errors;

        if (BitcoinNetwork.IsBuiltIn(Name))
            errors.Add($"CustomSignet:Name '{Name}' is a built-in network, not a custom signet.");
        if (!network.IsSignet)
            errors.Add($"CustomSignet:Name '{Name}' needs Node:Network signet, not '{network.Name}'.");

        return errors;
    }
}