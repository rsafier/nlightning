using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Domain.Node.Fencing;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Options;

/// <summary>
/// A minimal bitcoind JSON-RPC endpoint on a free local port: <c>getblockchaininfo</c> (the service's constructor
/// asks it) answered as a regtest Core 28 node, every other method by the given answer.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class FakeRpcNode : IDisposable
{
    private const string BlockchainInfo = """
        {"result":{"chain":"regtest","blocks":1,"headers":1,
          "bestblockhash":"0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206",
          "difficulty":4.656542373906925e-10,"time":1296688602,"mediantime":1296688602,
          "verificationprogress":1,"initialblockdownload":false,
          "chainwork":"0000000000000000000000000000000000000000000000000000000000000002",
          "size_on_disk":293,"pruned":false,"warnings":""},"error":null,"id":1}
        """;

    private readonly HttpListener _listener = new();
    private readonly Func<string, string> _answer;
    private readonly Dictionary<string, int> _calls = [];
    private readonly Task _loop;

    public FakeRpcNode(Func<string, string> answer)
    {
        _answer = answer;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    private int Port { get; }

    public int Calls(string method)
    {
        lock (_calls)
            return _calls.GetValueOrDefault(method);
    }

    public BitcoinChainService CreateService(ILogger<BitcoinChainService>? logger = null,
                                             INodeWriteFence? writeFence = null) =>
        new(new OptionsWrapper<BitcoinOptions>(new BitcoinOptions
        {
            RpcEndpoint = $"http://127.0.0.1:{Port}",
            RpcUser = "user",
            RpcPassword = "password",
            ZmqHost = "127.0.0.1",
            ZmqBlockPort = 1,
            ZmqTxPort = 1
        }),
            logger ?? NullLogger<BitcoinChainService>.Instance,
            new OptionsWrapper<NodeOptions>(new NodeOptions { BitcoinNetwork = new BitcoinNetwork("regtest") }),
            writeFence);

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was closed under the loop
        }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException
                                          or InvalidOperationException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var method = request["method"]!.Value<string>()!;
            lock (_calls)
                _calls[method] = _calls.GetValueOrDefault(method) + 1;

            var body = Encoding.UTF8.GetBytes(method == "getblockchaininfo" ? BlockchainInfo : _answer(method));
            // bitcoind's HTTP status: 404 for an unknown method, 500 for other errors
            context.Response.StatusCode = JObject.Parse(Encoding.UTF8.GetString(body))["error"] is JObject error
                                              ? error["code"]!.Value<int>() == -32601 ? 404 : 500
                                              : 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }
}