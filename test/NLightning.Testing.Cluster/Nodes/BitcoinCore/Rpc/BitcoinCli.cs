using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc;

using Kube;

/// <summary>
/// <c>bitcoin-cli</c> command lines and output, for <see cref="ExecCliRpcTransport"/> and the readiness probe. Calls use
/// <c>-named</c>: a string argument goes as it is, anything else as JSON (bitcoin-cli parses the JSON-typed parameters).
/// </summary>
public static partial class BitcoinCli
{
    /// <summary>The command line that calls <paramref name="method"/> on the local bitcoind.</summary>
    /// <param name="network">The chain flag's name (<c>regtest</c>).</param>
    /// <param name="rpcPort">The RPC port.</param>
    /// <param name="user">The RPC user.</param>
    /// <param name="password">The RPC password.</param>
    /// <param name="wallet">The wallet (<c>-rpcwallet</c>), or null.</param>
    /// <param name="method">The RPC method.</param>
    /// <param name="namedArgs">Named arguments; null values are left out.</param>
    public static IReadOnlyList<string> BuildCommand(string network, int rpcPort, string user, string password,
                                                     string? wallet, string method,
                                                     IReadOnlyDictionary<string, object?>? namedArgs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var command = new List<string>
        {
            "bitcoin-cli", $"-{network}", $"-rpcport={rpcPort.ToString(CultureInfo.InvariantCulture)}",
            $"-rpcuser={user}", $"-rpcpassword={password}"
        };
        if (wallet is not null)
            command.Add($"-rpcwallet={wallet}");
        command.Add("-named");
        command.Add(method);
        foreach (var (name, value) in namedArgs ?? new Dictionary<string, object?>())
        {
            if (value is not null)
                command.Add($"{name}={EncodeValue(value)}");
        }

        return command;
    }

    /// <summary>A named argument's value as bitcoin-cli takes it.</summary>
    public static string EncodeValue(object value) =>
        value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            IFormattable f and (int or long or uint or ulong or short or ushort or byte or sbyte) =>
                f.ToString(null, CultureInfo.InvariantCulture),
            double or float => throw new ArgumentException("Pass amounts and fee rates as decimal", nameof(value)),
            JToken token => token.ToString(Formatting.None),
            _ => JToken.FromObject(value).ToString(Formatting.None)
        };

    /// <summary>
    /// The call's result from bitcoin-cli's output: JSON for objects, arrays, numbers and booleans, nothing for a
    /// null result, and the raw text for a string (bitcoin-cli prints strings without quotes).
    /// </summary>
    /// <exception cref="BitcoinRpcException">bitcoin-cli failed (the RPC error's code and message when it printed them).</exception>
    public static JToken ParseResult(string method, ExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
            throw ParseError(method, result.ExitCode, result.StdErrText);

        var text = result.StdOutText.TrimEnd('\r', '\n');
        if (text.Length == 0)
            return JValue.CreateNull();
        if (text[0] is '{' or '[')
            return JToken.Parse(text);
        if (text is "true" or "false" or "null")
            return JToken.Parse(text);
        // A number, but never a 64-character hash that happens to be all digits
        if (text.Length <= 30 && Number().IsMatch(text))
            return JToken.Parse(text);

        return new JValue(text);
    }

    /// <summary>
    /// The exception for a failed bitcoin-cli run: <c>error code: -5</c> / <c>error message:</c> becomes the RPC
    /// error; anything else (<c>error: Could not connect to the server</c>) an error without a code.
    /// </summary>
    public static BitcoinRpcException ParseError(string method, int exitCode, string stdErr)
    {
        var match = ErrorCode().Match(stdErr);
        if (match.Success)
        {
            var code = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var message = ErrorMessage().Match(stdErr) is { Success: true } m ? m.Groups[1].Value.Trim() : stdErr.Trim();
            return new BitcoinRpcException(method, code, message);
        }

        var text = stdErr.Trim();
        return new BitcoinRpcException(method, null,
                                       text.Length == 0 ? $"bitcoin-cli exited with {exitCode}" : text);
    }

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex Number();

    [GeneratedRegex(@"error code:\s*(-?\d+)")]
    private static partial Regex ErrorCode();

    [GeneratedRegex(@"error message:\s*(.*)\z", RegexOptions.Singleline)]
    private static partial Regex ErrorMessage();
}