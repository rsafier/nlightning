using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Walletrpc;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using LnrpcAddressType = Lnrpc.AddressType;
using OutPoint = Lnrpc.OutPoint;
using WalletAddressType = Walletrpc.AddressType;

/// <summary>
/// LND's <c>walletrpc.WalletKit</c> over the node's wallet: PSBT funding/signing/publishing,
/// leases, transaction history and labels, wallet CPFP, imported scripts, and BIP84/BIP86 accounts.
/// Named owned accounts preserve their own coin-selection and change scope. Imported accounts remain watch-only.
/// </summary>
/// <remarks>
/// Wallet signing requires leased inputs and verifies their recorded ownership. Foreign PSBT maps are retained;
/// finalization requires foreign signatures and verifies the complete transaction. Unconfirmed selection is explicit
/// and uses the revalidated mempool catalogue. Nested/hybrid P2SH account scopes remain unsupported.
/// </remarks>
public sealed partial class WalletKitService : WalletKit.WalletKitBase
{
    private const string DefaultAccount = "default";

    /// <summary>bitcoind's default minimum relay fee, 1 sat/vB, in sat/kw.</summary>
    private const long MinRelayFeePerKw = 253;

    /// <summary>LND's <c>chanfunding.DefaultMaxFeeRatio</c>: FundPsbt's fee cap when <c>max_fee_ratio</c> is unset.</summary>
    internal const double DefaultMaxFeeRatio = 0.2;

    private readonly IFeeService _feeService;
    private readonly Network _network;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;

    public WalletKitService(IServiceProvider serviceProvider, IServiceScopeFactory scopeFactory,
                            IFeeService feeService, IOptions<NodeOptions> nodeOptions)
    {
        _serviceProvider = serviceProvider;
        _scopeFactory = scopeFactory;
        _feeService = feeService;
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    }

    /// <summary>The PSBT service, registered with <c>withdraw</c>; without it the wallet methods are unavailable.</summary>
    private IWalletPsbtService Psbt =>
        _serviceProvider.GetService<IWalletPsbtService>()
     ?? throw new RpcException(new Status(StatusCode.Unavailable, "this node has no wallet PSBT service"));

    /// <inheritdoc />
    public override async Task<ListUnspentResponse> ListUnspent(ListUnspentRequest request, ServerCallContext context)
    {
        await CheckAccountAsync(request.Account, context.CancellationToken);
        var (min, max) = ParseConfs(request.MinConfs, request.MaxConfs, request.UnconfirmedOnly);
        HashSet<string>? accountScripts = null;
        if (request.Account.Length > 0 && request.Account != DefaultAccount)
        {
            await using var accountScope = _scopeFactory.CreateAsyncScope();
            var accountUow = accountScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var account = await accountUow.WalletAccountDbRepository.GetAsync(request.Account, context.CancellationToken);
            if (account?.WatchOnly == true)
            {
                accountScripts = new HashSet<string>(StringComparer.Ordinal);
                var accountService = accountScope.ServiceProvider.GetRequiredService<Infrastructure.Bitcoin.Wallet.WalletAccountService>();
                foreach (var change in new[] { false, true })
                    for (uint index = 0; index < (change ? account.InternalKeyCount : account.ExternalKeyCount) + Infrastructure.Bitcoin.Wallet.BitcoinWalletService.GapLimit; index++)
                        accountScripts.Add(Convert.ToHexString(BitcoinAddress.Create(accountService.Address(account, change, index), _network).ScriptPubKey.ToBytes()));
            }
            else accountScripts = [];
        }
        var response = new ListUnspentResponse();
        var listed = new HashSet<(TxId TxId, uint Index)>();
        foreach (var utxo in await Run(() => Psbt.ListUnspentAsync(min, max, context.CancellationToken)))
        {
            if (request.Account.Length > 0 && request.Account != utxo.Account) continue;
            if (!listed.Add((utxo.TxId, utxo.Index)))
                continue;
            response.Utxos.Add(new Lnrpc.Utxo
            {
                AddressType = utxo.AddressType == AddressType.P2Tr
                                  ? LnrpcAddressType.TaprootPubkey
                                  : LnrpcAddressType.WitnessPubkeyHash,
                Address = utxo.Address,
                AmountSat = utxo.Amount.Satoshi,
                PkScript = Convert.ToHexString((byte[])utxo.ScriptPubKey).ToLowerInvariant(),
                Outpoint = ToOutPoint(utxo.TxId, utxo.Index),
                Confirmations = utxo.Confirmations
            });
        }
        if (_serviceProvider.GetService<Infrastructure.Bitcoin.Wallet.Imports.ImportedTapscriptTracker>() is { } imported)
        {
            var snapshot = await Run(() => imported.SnapshotAsync(context.CancellationToken));
            foreach (var output in snapshot.Outputs)
            {
                if (accountScripts is not null && !accountScripts.Contains(Convert.ToHexString(output.Output.ScriptPubKey.ToBytes()))) continue;
                var confirmations = snapshot.Tip - output.Height + 1;
                if (confirmations < min || confirmations > max ||
                    !listed.Add((new TxId(output.Outpoint.Hash.ToBytes()), output.Outpoint.N))) continue;
                response.Utxos.Add(new Lnrpc.Utxo
                {
                    AddressType = ImportedAddressType(output.Output.ScriptPubKey),
                    Address = output.Output.ScriptPubKey.GetDestinationAddress(_network)!.ToString(),
                    AmountSat = output.Output.Value.Satoshi,
                    PkScript = Convert.ToHexStringLower(output.Output.ScriptPubKey.ToBytes()),
                    Outpoint = ToOutPoint(new TxId(output.Outpoint.Hash.ToBytes()), output.Outpoint.N),
                    Confirmations = confirmations
                });
            }
        }
        return response;
    }

    /// <summary>The LND address type of an imported output's script (NL-1186: P2TR, P2WPKH or nested P2WPKH).</summary>
    private static LnrpcAddressType ImportedAddressType(Script script) =>
        script.IsScriptType(ScriptType.P2WPKH) ? LnrpcAddressType.WitnessPubkeyHash
        : script.IsScriptType(ScriptType.P2SH) ? LnrpcAddressType.NestedPubkeyHash
        : LnrpcAddressType.TaprootPubkey;

    /// <inheritdoc />
    public override async Task<AddrResponse> NextAddr(AddrRequest request, ServerCallContext context)
    {
        var type = request.Type switch
        {
            WalletAddressType.Unknown or WalletAddressType.WitnessPubkeyHash => AddressType.P2Wpkh,
            WalletAddressType.TaprootPubkey => AddressType.P2Tr,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                   $"address type {request.Type} is not supported"))
        };

        // Every handed-out address is reserved and never handed out again (NL-280)
        await using var scope = _scopeFactory.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var address = request.Account.Length == 0 || request.Account == DefaultAccount
            ? await wallet.GetUnusedAddressAsync(type, request.Change)
            : await Run(() => scope.ServiceProvider.GetRequiredService<Infrastructure.Bitcoin.Wallet.WalletAccountService>()
                .NextAsync(request.Account, type, request.Change, context.CancellationToken));
        return new AddrResponse { Addr = address.Address };
    }

    /// <inheritdoc />
    public override async Task<EstimateFeeResponse> EstimateFee(EstimateFeeRequest request, ServerCallContext context)
    {
        if (request.ConfTarget < 2)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "confirmation target must be greater than 1"));

        var rate = await _feeService.GetFeeRatePerKwAsync((uint)request.ConfTarget, context.CancellationToken);

        // bitcoind's mempoolminfee (at least its relay floor, 253 sat/kw = 1 sat/vB), as LND reports its backend's
        var minRelay = _serviceProvider.GetService<IBitcoinChainService>() is { } chain
                           ? await chain.GetMempoolMinFeeRatePerKwAsync() ?? MinRelayFeePerKw
                           : MinRelayFeePerKw;
        return new EstimateFeeResponse
        {
            SatPerKw = rate.Satoshi,
            MinRelayFeeSatPerKw = Math.Max(MinRelayFeePerKw, minRelay)
        };
    }

    /// <inheritdoc />
    public override async Task<LeaseOutputResponse> LeaseOutput(LeaseOutputRequest request, ServerCallContext context)
    {
        var (txId, index) = FromOutPoint(request.Outpoint);
        var duration = request.ExpirationSeconds == 0
                           ? TimeSpan.Zero
                           : TimeSpan.FromSeconds(Math.Min(request.ExpirationSeconds, int.MaxValue));
        var lease = await Run(() => request.ReleaseAfterSpendConfs == 0
            ? Psbt.LeaseAsync(request.Id.ToByteArray(), txId, index, duration, context.CancellationToken)
            : Psbt.LeaseAsync(request.Id.ToByteArray(), txId, index, duration, request.ReleaseAfterSpendConfs,
                context.CancellationToken));
        return new LeaseOutputResponse { Expiration = (ulong)lease.Expiration.ToUnixTimeSeconds() };
    }

    /// <inheritdoc />
    public override async Task<ReleaseOutputResponse> ReleaseOutput(ReleaseOutputRequest request,
                                                                     ServerCallContext context)
    {
        var (txId, index) = FromOutPoint(request.Outpoint);
        await Run(async () =>
        {
            await Psbt.ReleaseAsync(request.Id.ToByteArray(), txId, index, context.CancellationToken);
            return true;
        });
        return new ReleaseOutputResponse { Status = "released" };
    }

    /// <inheritdoc />
    public override async Task<ListLeasesResponse> ListLeases(ListLeasesRequest request, ServerCallContext context)
    {
        var response = new ListLeasesResponse();
        response.LockedUtxos.AddRange((await Run(() => Psbt.ListLeasesAsync(context.CancellationToken)))
                                     .Select(ToRpc));
        return response;
    }

    /// <inheritdoc />
    public override async Task<FundPsbtResponse> FundPsbt(FundPsbtRequest request, ServerCallContext context)
    {
        Domain.Bitcoin.Wallet.Models.WalletAccountModel? namedAccount = null;
        if (request.Account.Length > 0 && request.Account != DefaultAccount)
        {
            await using var accountScope = _scopeFactory.CreateAsyncScope();
            namedAccount = await accountScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .WalletAccountDbRepository.GetAsync(request.Account, context.CancellationToken)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "account not found"));
            if (namedAccount.WatchOnly) throw new RpcException(new Status(StatusCode.FailedPrecondition, "watch-only account cannot fund a PSBT"));
            if (request.ChangeType != ChangeAddressType.Unspecified)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Custom accounts use their own change scope."));
        }
        if (request.SpendUnconfirmed && request.MinConfs != 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "spend_unconfirmed requires min_confs=0"));
        CheckCoinSelectionStrategy(request.CoinSelectionStrategy);
        if (double.IsNaN(request.MaxFeeRatio) || request.MaxFeeRatio is < 0 or > 1)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              $"max fee ratio {request.MaxFeeRatio} must be between 0 and 1"));
        // LND v0.21: an unset max_fee_ratio is chanfunding.DefaultMaxFeeRatio, and sanityCheckFee always applies
        var maxFeeRatio = request.MaxFeeRatio == 0 ? DefaultMaxFeeRatio : request.MaxFeeRatio;

        var lockId = request.CustomLockId.IsEmpty
                         ? Infrastructure.Bitcoin.Wallet.WalletPsbtService.DefaultLockId
                         : request.CustomLockId.ToByteArray();
        var duration = request.LockExpirationSeconds == 0
                           ? TimeSpan.Zero
                           : TimeSpan.FromSeconds(Math.Min(request.LockExpirationSeconds, int.MaxValue));
        var feeRatePerKw = await GetFeeRatePerKwAsync(request, context.CancellationToken);

        List<(BitcoinScript, LightningMoney)> outputs = [];
        List<(TxId, uint)> inputs = [];
        uint lockTime = 0;
        var version = 2;
        byte[]? templatePsbt = null;
        int? existingChangeIndex = null;
        switch (request.TemplateCase)
        {
            case FundPsbtRequest.TemplateOneofCase.Raw:
                foreach (var input in request.Raw.Inputs)
                    inputs.Add(FromOutPoint(input));
                foreach (var (address, sat) in request.Raw.Outputs.OrderBy(o => o.Key, StringComparer.Ordinal))
                    outputs.Add((ParseAddress(address), LightningMoney.Satoshis(sat)));
                break;
            case FundPsbtRequest.TemplateOneofCase.Psbt:
                PSBT template;
                try
                {
                    template = PSBT.Load(request.Psbt.ToByteArray(), _network);
                }
                catch (FormatException e)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"the PSBT does not parse: {e.Message}"));
                }

                var tx = template.GetGlobalTransaction();
                lockTime = tx.LockTime.Value;
                version = (int)tx.Version;
                foreach (var input in tx.Inputs)
                    inputs.Add((new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N));
                foreach (var output in tx.Outputs)
                    outputs.Add((output.ScriptPubKey.ToBytes(), LightningMoney.Satoshis(output.Value.Satoshi)));
                break;
            case FundPsbtRequest.TemplateOneofCase.CoinSelect:
                templatePsbt = request.CoinSelect.Psbt.ToByteArray();
                if (request.CoinSelect.ChangeOutputCase == PsbtCoinSelect.ChangeOutputOneofCase.ExistingOutputIndex)
                    existingChangeIndex = request.CoinSelect.ExistingOutputIndex;
                else if (request.CoinSelect.ChangeOutputCase != PsbtCoinSelect.ChangeOutputOneofCase.Add || !request.CoinSelect.Add)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "coin_select must name existing change or request a new change output"));
                break;
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument, "no template given"));
        }

        var changeType = namedAccount?.AddressType ?? (request.ChangeType == ChangeAddressType.P2Tr ? AddressType.P2Tr : AddressType.P2Wpkh);
        var result = await Run(() => Psbt.FundPsbtAsync(new PsbtFundRequest(outputs, inputs, feeRatePerKw,
                                                                            request.MinConfs, lockId, duration,
                                                                            lockTime, version, changeType,
                                                                            maxFeeRatio, templatePsbt, existingChangeIndex,
                                                                            request.TemplateCase == FundPsbtRequest.TemplateOneofCase.CoinSelect,
                                                                            request.SpendUnconfirmed, request.InputReleaseAfterSpendConfs,
                                                                            request.Account.Length == 0 ? DefaultAccount : request.Account),
                                                        context.CancellationToken));
        var response = new FundPsbtResponse
        {
            FundedPsbt = ByteString.CopyFrom(result.Psbt),
            ChangeOutputIndex = result.ChangeOutputIndex
        };
        response.LockedUtxos.AddRange(result.Leases.Select(ToRpc));
        return response;
    }

    /// <inheritdoc />
    public override async Task<FinalizePsbtResponse> FinalizePsbt(FinalizePsbtRequest request,
                                                                  ServerCallContext context)
    {
        await CheckAccountAsync(request.Account, context.CancellationToken);
        var result = await Run(() => Psbt.FinalizePsbtAsync(request.FundedPsbt.ToByteArray(),
                                                            context.CancellationToken));
        return new FinalizePsbtResponse
        {
            SignedPsbt = ByteString.CopyFrom(result.SignedPsbt),
            RawFinalTx = ByteString.CopyFrom(result.RawFinalTx)
        };
    }

    /// <inheritdoc />
    public override async Task<PublishResponse> PublishTransaction(Walletrpc.Transaction request,
                                                                   ServerCallContext context)
    {
        if (request.TxHex.IsEmpty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "need to provide a transaction"));
        if (request.Label.Length > 0)
            CheckLabel(request.Label);

        // LND: a refused publish is an RPC error (UNKNOWN, LND's plain error text); publish_error stays empty on success
        await Run(() => Psbt.PublishAsync(request.TxHex.ToByteArray(), request.Label, context.CancellationToken));
        return new PublishResponse();
    }

    /// <summary>
    /// The coin selection strategies the wallet has: largest first (its only one, so also the global configuration);
    /// LND's random selection is refused.
    /// </summary>
    internal static void CheckCoinSelectionStrategy(Lnrpc.CoinSelectionStrategy strategy)
    {
        if (strategy is not (Lnrpc.CoinSelectionStrategy.StrategyUseGlobalConfig
                          or Lnrpc.CoinSelectionStrategy.StrategyLargest))
            throw new RpcException(new Status(StatusCode.Unimplemented,
                                              $"coin selection strategy {strategy} is not supported; the wallet selects "
                                            + "the largest outputs first"));
    }

    /// <summary>LND's <c>ParseConfs</c>.</summary>
    internal static (uint Min, uint Max) ParseConfs(int minConfs, int maxConfs, bool unconfirmedOnly)
    {
        if (unconfirmedOnly)
        {
            if (minConfs != 0 || maxConfs != 0)
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                  "min/max confs must be zero with unconfirmed_only"));
            return (0, 0);
        }

        if (minConfs < 0 || maxConfs < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "min/max confs cannot be negative"));

        var max = maxConfs == 0 ? int.MaxValue : maxConfs;
        if (minConfs > max)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "max confs must be greater than or equal to min confs"));
        return ((uint)minConfs, (uint)max);
    }

    private async Task<long> GetFeeRatePerKwAsync(FundPsbtRequest request, CancellationToken cancellationToken) =>
        request.FeesCase switch
        {
            FundPsbtRequest.FeesOneofCase.SatPerVbyte => checked((long)request.SatPerVbyte * 250),
            FundPsbtRequest.FeesOneofCase.SatPerKw => checked((long)request.SatPerKw),
            FundPsbtRequest.FeesOneofCase.TargetConf =>
                (await _feeService.GetFeeRatePerKwAsync(request.TargetConf, cancellationToken)).Satoshi,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "fee rate or conf target required"))
        };

    private BitcoinScript ParseAddress(string address)
    {
        try
        {
            return BitcoinAddress.Create(address, _network).ScriptPubKey.ToBytes();
        }
        catch (FormatException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"invalid address {address}"));
        }
    }

    private async Task CheckAccountAsync(string account, CancellationToken ct)
    {
        if (account.Length == 0 || account == DefaultAccount) return;
        await using var scope = _scopeFactory.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().WalletAccountDbRepository.GetAsync(account, ct) is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"account {account} not found"));
    }

    /// <summary>A wallet call with its refusals as gRPC statuses.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        catch (WalletSpendException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }
        catch (WalletPsbtException e)
        {
            throw new RpcException(new Status(e.Error switch
            {
                WalletPsbtError.InvalidArgument => StatusCode.InvalidArgument,
                WalletPsbtError.NotFound => StatusCode.NotFound,
                WalletPsbtError.PublishRefused => StatusCode.Unknown,
                _ => StatusCode.FailedPrecondition
            }, e.Message));
        }
    }

    internal static OutPoint ToOutPoint(TxId txId, uint index) => new()
    {
        TxidBytes = ByteString.CopyFrom((byte[])txId),
        TxidStr = txId.ToString(),
        OutputIndex = index
    };

    /// <summary>An outpoint by <c>txid_bytes</c> (internal byte order) or <c>txid_str</c> (display order).</summary>
    internal static (TxId TxId, uint Index) FromOutPoint(OutPoint? outPoint)
    {
        if (outPoint is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "outpoint required"));

        if (outPoint.TxidBytes.Length == 32)
            return (new TxId(outPoint.TxidBytes.ToByteArray()), outPoint.OutputIndex);

        if (uint256.TryParse(outPoint.TxidStr, out var hash))
            return (new TxId(hash.ToBytes()), outPoint.OutputIndex);

        throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid outpoint txid"));
    }

    private static UtxoLease ToRpc(WalletLease lease) => new()
    {
        Id = ByteString.CopyFrom(lease.LockId),
        Outpoint = ToOutPoint(lease.TxId, lease.Index),
        Expiration = (ulong)lease.Expiration.ToUnixTimeSeconds(),
        PkScript = ByteString.CopyFrom((byte[])lease.ScriptPubKey),
        Value = (ulong)lease.Amount.Satoshi
    };
}