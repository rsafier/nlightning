using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ExportChanBackupIpcHandler
    : ClientCommandIpcHandler<ExportChanBackupIpcRequest, ExportChanBackupClientRequest,
        ExportChanBackupClientResponse, ExportChanBackupIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ExportChanBackup;

    public ExportChanBackupIpcHandler(ILogger<ExportChanBackupIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ExportChanBackupClientRequest ToClientRequest(ExportChanBackupIpcRequest request) =>
        request.ToClientRequest();

    protected override ExportChanBackupIpcResponse ToIpcResponse(ExportChanBackupClientResponse response) =>
        ExportChanBackupIpcResponse.FromClientResponse(response);
}

internal sealed class VerifyChanBackupIpcHandler
    : ClientCommandIpcHandler<VerifyChanBackupIpcRequest, VerifyChanBackupClientRequest,
        VerifyChanBackupClientResponse, VerifyChanBackupIpcResponse>
{
    public override ClientCommand Command => ClientCommand.VerifyChanBackup;

    public VerifyChanBackupIpcHandler(ILogger<VerifyChanBackupIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override VerifyChanBackupClientRequest ToClientRequest(VerifyChanBackupIpcRequest request) =>
        request.ToClientRequest();

    protected override VerifyChanBackupIpcResponse ToIpcResponse(VerifyChanBackupClientResponse response) =>
        VerifyChanBackupIpcResponse.FromClientResponse(response);
}