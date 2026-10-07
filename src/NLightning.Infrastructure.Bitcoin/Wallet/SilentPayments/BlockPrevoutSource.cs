using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Wallet.SilentPayments;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Node.Options;
using Networks;
using Options;

/// <summary>Full-node undo data, or bounded transaction lookup, without trusting remote scan services.</summary>
public sealed class BlockPrevoutSource : IBlockPrevoutSource
{
    private const int CacheCapacity = 256;
    private readonly HttpClient _http;
    private readonly Uri _address;
    private readonly Network _network;
    private readonly string _authorization;
    private readonly SilentPaymentPrevoutSource _requested;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<uint256, LinkedListNode<(uint256 Id, Transaction Tx)>> _cache = [];
    private readonly LinkedList<(uint256 Id, Transaction Tx)> _lru = [];
    private bool _probed;

    public BlockPrevoutSource(IOptions<BitcoinOptions> bitcoinOptions, IOptions<NodeOptions> nodeOptions,
                             SilentPaymentPrevoutSource source = SilentPaymentPrevoutSource.Auto)
    {
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
        _requested = source;
        Source = source;
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
        var options = bitcoinOptions.Value;
        var rpc = new RPCClient(new RPCCredentialString
        {
            UserPassword = new NetworkCredential(options.RpcUser, options.RpcPassword)
        }, options.RpcEndpoint, _network);
        _http = rpc.HttpClient;
        _address = rpc.Address;
        _authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.RpcUser}:{options.RpcPassword}"));
    }

    internal BlockPrevoutSource(HttpClient http, Uri address, Network network, SilentPaymentPrevoutSource source)
    {
        _http = http;
        _address = address;
        _network = network;
        _requested = source;
        Source = source;
        _authorization = "";
    }

    public SilentPaymentPrevoutSource Source { get; private set; }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ProbeCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProbeCoreAsync(CancellationToken cancellationToken)
    {
        if (_probed)
            return;
        var tip = uint256.Parse((string)(await RpcAsync("getbestblockhash", [], cancellationToken))!);
        var block = Block.Parse((string)(await RpcAsync("getblock", [tip.ToString(), 0], cancellationToken))!, _network);
        List<Exception> failures = [];
        var candidates = _requested == SilentPaymentPrevoutSource.Auto
            ? new[] { SilentPaymentPrevoutSource.GetBlock, SilentPaymentPrevoutSource.Rest, SilentPaymentPrevoutSource.GetRawTransaction }
            : [_requested];
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (candidate)
                {
                    case SilentPaymentPrevoutSource.GetBlock:
                        await ReadVerboseAsync(block, cancellationToken, requireAll: true);
                        break;
                    case SilentPaymentPrevoutSource.Rest:
                        await ReadRestAsync(block, cancellationToken);
                        break;
                    case SilentPaymentPrevoutSource.GetRawTransaction:
                        // A confirmed transaction proves lookup works even when the tip has no SP candidates.
                        var input = block.Transactions.Where(t => !t.IsCoinBase).SelectMany(t => t.Inputs).FirstOrDefault();
                        await GetTransactionAsync(input?.PrevOut.Hash ?? block.Transactions[0].GetHash(), cancellationToken);
                        break;
                }
                Source = candidate;
                _probed = true;
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }
        throw new InvalidOperationException("Silent payment receiving cannot read block prevouts. Enable Core undo data "
                                            + "(getblock 3), -rest, or confirmed getrawtransaction lookup (-txindex on Core).",
                                            new AggregateException(failures));
    }

    public async Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> GetPrevoutsAsync(
        BitcoinBlock block, uint height, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var parsed = Block.Load(block.BlockData, _network);
            if (!parsed.GetHash().ToBytes().AsSpan().SequenceEqual((byte[])block.BlockHash))
                throw new InvalidDataException("Block bytes do not match the requested block hash.");
            if (!parsed.Transactions.Any(IsCandidate))
                return new Dictionary<TxId, IReadOnlyList<BitcoinPrevout>>();
            await ProbeCoreAsync(cancellationToken);
            await ValidateHeightAsync(height, cancellationToken);
            return Source switch
            {
                SilentPaymentPrevoutSource.GetBlock => await ReadVerboseAsync(parsed, cancellationToken),
                SilentPaymentPrevoutSource.Rest => await ReadRestAsync(parsed, cancellationToken),
                SilentPaymentPrevoutSource.GetRawTransaction => await ReadTransactionsAsync(parsed, cancellationToken),
                _ => throw new InvalidOperationException("No prevout source selected.")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ValidateHeightAsync(uint height, CancellationToken cancellationToken = default)
    {
        var chain = (JObject)await RpcAsync("getblockchaininfo", [], cancellationToken);
        if ((bool?)chain["pruned"] == true && height < (uint?)chain["pruneheight"])
            throw new InvalidOperationException($"Silent payment block {height} is pruned; use an unpruned node to rescan.");
    }

    private static bool IsCandidate(Transaction tx) => !tx.IsCoinBase && tx.Outputs.Any(o =>
    {
        var script = o.ScriptPubKey.ToBytes();
        return script.Length == 34 && script[0] == 0x51 && script[1] == 0x20;
    });

    private async Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> ReadVerboseAsync(
        Block block, CancellationToken cancellationToken, bool requireAll = false)
    {
        using var request = CreateRequest("getblock", [block.GetHash().ToString(), 3]);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var text = new StreamReader(stream);
        return await ParseVerboseAsync(text, block, requireAll, cancellationToken);
    }

    // Stream the envelope and array; only one transaction's JSON is materialized at a time.
    internal static async Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> ParseVerboseAsync(
        TextReader text, Block block, bool requireAll = false, CancellationToken cancellationToken = default)
    {
        using var reader = new JsonTextReader(text);
        Dictionary<TxId, IReadOnlyList<BitcoinPrevout>> result = [];
        var candidates = block.Transactions.Where(t => !t.IsCoinBase && (requireAll || IsCandidate(t))).ToDictionary(t => t.GetHash().ToString());
        var sawArray = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.TokenType != JsonToken.PropertyName)
                continue;
            var property = (string)reader.Value!;
            if (reader.Depth == 1 && property == "error")
            {
                await reader.ReadAsync(cancellationToken);
                if (reader.TokenType != JsonToken.Null)
                    throw new InvalidOperationException($"getblock 3 failed: {await JToken.LoadAsync(reader, cancellationToken)}");
            }
            else if (reader.Depth == 2 && property == "tx")
            {
                if (!await reader.ReadAsync(cancellationToken) || reader.TokenType != JsonToken.StartArray)
                    throw new InvalidDataException("getblock 3 returned no transaction array.");
                sawArray = true;
                while (await reader.ReadAsync(cancellationToken) && reader.TokenType != JsonToken.EndArray)
                {
                    if (reader.TokenType != JsonToken.StartObject)
                        throw new InvalidDataException("getblock 3 must return transaction objects.");
                    var json = await JObject.LoadAsync(reader, cancellationToken);
                    if (!candidates.TryGetValue((string?)json["txid"] ?? "", out var tx))
                        continue;
                    var inputs = json["vin"] as JArray;
                    if (inputs is null || inputs.Count != tx.Inputs.Count)
                        throw new InvalidDataException("getblock 3 input count mismatch.");
                    List<BitcoinPrevout> prevouts = [];
                    for (var i = 0; i < inputs.Count; i++)
                    {
                        if ((string?)inputs[i]["txid"] != tx.Inputs[i].PrevOut.Hash.ToString()
                            || (uint?)inputs[i]["vout"] != tx.Inputs[i].PrevOut.N)
                            throw new InvalidDataException("getblock 3 input outpoint mismatch.");
                        var previous = inputs[i]["prevout"];
                        var hex = (string?)previous?["scriptPubKey"]?["hex"];
                        var value = (decimal?)previous?["value"];
                        if (hex is null || value is null || value < 0 || value * 100_000_000m != decimal.Truncate(value.Value * 100_000_000m))
                            throw new InvalidDataException("getblock 3 is missing an exact prevout; undo data may be pruned.");
                        prevouts.Add(new BitcoinPrevout(checked((ulong)(value.Value * 100_000_000m)), new BitcoinScript(Convert.FromHexString(hex))));
                    }
                    result.Add(new TxId(tx.GetHash().ToBytes()), prevouts);
                }
            }
        }
        if (!sawArray || result.Count != candidates.Count)
            throw new InvalidDataException("getblock 3 did not supply every candidate transaction.");
        return result;
    }

    private async Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> ReadRestAsync(
        Block block, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_address,
            $"/rest/spenttxouts/{block.GetHash()}.bin"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"REST spenttxouts unavailable (HTTP {(int)response.StatusCode}); enable -rest; undo data may be pruned.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return ParseRest(stream, block);
    }

    // Core 31 rest.cpp SerializeBlockUndo: CompactSize tx count (coinbase included), then per-tx
    // CompactSize input count and ordinary CTxOut serialization (int64 satoshis, CompactSize script bytes).
    internal static IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>> ParseRest(Stream stream, Block block)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (ReadSize(reader) != (ulong)block.Transactions.Count)
            throw new InvalidDataException("REST spenttxouts transaction count mismatch.");
        Dictionary<TxId, IReadOnlyList<BitcoinPrevout>> result = [];
        foreach (var tx in block.Transactions)
        {
            var count = ReadSize(reader);
            if (count != (ulong)(tx.IsCoinBase ? 0 : tx.Inputs.Count))
                throw new InvalidDataException("REST spenttxouts input count mismatch.");
            var candidate = IsCandidate(tx);
            List<BitcoinPrevout> previous = [];
            for (ulong i = 0; i < count; i++)
            {
                var amount = reader.ReadInt64();
                var length = ReadSize(reader);
                if (amount < 0 || length > 10_000)
                    throw new InvalidDataException("Invalid REST previous output.");
                var script = reader.ReadBytes((int)length);
                if (script.Length != (int)length)
                    throw new EndOfStreamException("Truncated REST previous output script.");
                if (candidate)
                    previous.Add(new BitcoinPrevout((ulong)amount, new BitcoinScript(script)));
            }
            if (candidate)
                result.Add(new TxId(tx.GetHash().ToBytes()), previous);
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Unexpected bytes after REST spenttxouts.");
        return result;
    }

    private static ulong ReadSize(BinaryReader reader)
    {
        var first = reader.ReadByte();
        var value = first switch { 253 => reader.ReadUInt16(), 254 => reader.ReadUInt32(), 255 => reader.ReadUInt64(), _ => (ulong)first };
        if ((first == 253 && value < 253) || (first == 254 && value <= ushort.MaxValue) || (first == 255 && value <= uint.MaxValue))
            throw new InvalidDataException("Noncanonical CompactSize in REST spenttxouts.");
        return value;
    }

    private async Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> ReadTransactionsAsync(
        Block block, CancellationToken cancellationToken)
    {
        Dictionary<OutPoint, TxOut> earlier = [];
        Dictionary<TxId, IReadOnlyList<BitcoinPrevout>> result = [];
        foreach (var tx in block.Transactions)
        {
            if (IsCandidate(tx))
            {
                List<BitcoinPrevout> prevouts = [];
                foreach (var input in tx.Inputs)
                {
                    if (!earlier.TryGetValue(input.PrevOut, out var output))
                    {
                        var previous = await GetTransactionAsync(input.PrevOut.Hash, cancellationToken);
                        if (input.PrevOut.N >= previous.Outputs.Count)
                            throw new InvalidDataException("getrawtransaction prevout index does not exist.");
                        output = previous.Outputs[(int)input.PrevOut.N];
                    }
                    prevouts.Add(new BitcoinPrevout(checked((ulong)output.Value.Satoshi), new BitcoinScript(output.ScriptPubKey.ToBytes())));
                }
                result.Add(new TxId(tx.GetHash().ToBytes()), prevouts);
            }
            for (var i = 0; i < tx.Outputs.Count; i++)
                earlier.Add(new OutPoint(tx.GetHash(), (uint)i), tx.Outputs[i]);
        }
        return result;
    }

    private async Task<Transaction> GetTransactionAsync(uint256 id, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(id, out var existing))
        {
            _lru.Remove(existing);
            _lru.AddFirst(existing);
            return existing.Value.Tx;
        }
        var raw = (string)(await RpcAsync("getrawtransaction", [id.ToString(), false], cancellationToken))!;
        var transaction = Transaction.Parse(raw, _network);
        if (transaction.GetHash() != id)
            throw new InvalidDataException("getrawtransaction answered a different transaction.");
        var added = _lru.AddFirst((id, transaction));
        _cache.Add(id, added);
        if (_cache.Count > CacheCapacity)
        {
            _cache.Remove(_lru.Last!.Value.Id);
            _lru.RemoveLast();
        }
        return transaction;
    }

    private HttpRequestMessage CreateRequest(string method, object[] parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);
        request.Content = new StringContent(JsonConvert.SerializeObject(new { jsonrpc = "1.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<JToken> RpcAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, parameters);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var text = new StreamReader(stream);
        using var reader = new JsonTextReader(text);
        var envelope = await JObject.LoadAsync(reader, cancellationToken);
        if (envelope["error"] is { Type: not JTokenType.Null } error)
            throw new InvalidOperationException($"{method} failed: {error}");
        if (!response.IsSuccessStatusCode || envelope["result"] is not { Type: not JTokenType.Null } result)
            throw new InvalidOperationException($"{method} returned no result (HTTP {(int)response.StatusCode}).");
        return result;
    }
}