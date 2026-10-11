using MessagePack;
using NLightning.Client;
using NLightning.Transport.Ipc.MessagePack;

// Register the default formatter for MessagePackSerializer
MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await ClientApp.RunAsync(args, cts.Token);