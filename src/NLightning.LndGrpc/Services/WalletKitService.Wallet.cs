using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.LndGrpc.Services;

using Domain.Accounting.Labels;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Exceptions;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Walletrpc;

/// <summary>
/// The walletrpc methods of NL-1186: <c>SignPsbt</c>, <c>GetTransaction</c>, <c>LabelTransaction</c>,
/// <c>RemoveTransaction</c>, <c>RequiredReserve</c>, <c>SubmitPackage</c>, <c>SignMessageWithAddr</c> and
/// <c>VerifyMessageWithAddr</c>.
/// </summary>
public sealed partial class WalletKitService
{
    /// <summary>LND's label limit (<c>labels.LabelLimit</c>), in characters.</summary>
    private const int LndLabelLimit = 500;

    /// <summary>
    /// <c>SignPsbt</c>: signs every input of the PSBT that is a wallet output leased here (P2WPKH: a partial signature;
    /// P2TR: the key path signature) and leaves every other input as it is, finalizing nothing. A wallet input that is
    /// not leased, or is reserved for another spend of this node, refuses the whole PSBT: only leased wallet outputs are
    /// ever signed (NL-1184).
    /// </summary>
    public override async Task<SignPsbtResponse> SignPsbt(SignPsbtRequest request, ServerCallContext context)
    {
        if (request.FundedPsbt.IsEmpty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "funded_psbt is required"));

        var result = await Run(() => Psbt.SignPsbtAsync(request.FundedPsbt.ToByteArray(), context.CancellationToken));
        var response = new SignPsbtResponse { SignedPsbt = ByteString.CopyFrom(result.SignedPsbt) };
        response.SignedInputs.AddRange(result.SignedInputs);
        return response;
    }

    /// <summary>
    /// <c>GetTransaction</c>: the wallet history entry of one transaction (<c>GetTransactions</c>' shape and sources);
    /// <c>NOT_FOUND</c> when the wallet has none.
    /// </summary>
    public override async Task<Lnrpc.Transaction> GetTransaction(GetTransactionRequest request,
                                                                 ServerCallContext context)
    {
        var txId = ParseTxId(request.Txid);
        var lightning = _serviceProvider.GetService<LightningService>()
                     ?? throw new RpcException(new Status(StatusCode.Unavailable,
                                                          "the wallet history is not available"));
        var details = await lightning.ListWalletTransactionsAsync(new Lnrpc.GetTransactionsRequest(), new HashSet<TxId> { txId }, false,
                                                                  context.CancellationToken);
        return details.Transactions.Count > 0
                   ? details.Transactions[0]
                   : throw new RpcException(new Status(StatusCode.NotFound,
                                                       $"transaction {request.Txid} not found in the wallet"));
    }

    /// <summary>
    /// <c>LabelTransaction</c>: the durable label of a known wallet transaction, including deposits and imported history,
    /// replaced only with <c>overwrite</c>. Independent label rows survive confirmation changes and raw-history cleanup;
    /// a label is at most LND's 500 characters and this node's 256
    /// UTF-8 bytes without control characters.
    /// </summary>
    public override async Task<LabelTransactionResponse> LabelTransaction(LabelTransactionRequest request,
                                                                         ServerCallContext context)
    {
        if (request.Txid.Length != 32)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txid must be 32 bytes"));
        if (request.Label.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "cannot label transaction with empty label"));
        CheckLabel(request.Label);

        var txId = new TxId(request.Txid.ToByteArray());
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var row = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId);
        var storedLabel = await unitOfWork.WalletTransactionDbRepository.GetLabelAsync(txId, context.CancellationToken);
        if ((!string.IsNullOrEmpty(storedLabel) || !string.IsNullOrEmpty(row?.Label)) && !request.Overwrite)
            throw new RpcException(new Status(StatusCode.AlreadyExists, "transaction already labelled"));
        if (row is null && (_serviceProvider.GetService<LightningService>() is not { } lightning ||
            (await lightning.ListWalletTransactionsAsync(new Lnrpc.GetTransactionsRequest(), new HashSet<TxId> { txId }, false,
                context.CancellationToken)).Transactions.Count == 0))
            throw new RpcException(new Status(StatusCode.NotFound, "cannot label transaction not known to wallet"));
        if (!await unitOfWork.WalletTransactionDbRepository.StageLabelAsync(txId, request.Label, request.Overwrite,
            context.CancellationToken))
            throw new RpcException(new Status(StatusCode.AlreadyExists, "transaction already labelled"));
        await unitOfWork.SaveChangesAsync();
        return new LabelTransactionResponse { Status = $"transaction label '{request.Label}' added" };
    }

    /// <summary>
    /// <c>RemoveTransaction</c>: stops rebroadcasting an unconfirmed wallet spend published here (a
    /// <c>PublishTransaction</c>, <c>SendOutputs</c> or <c>withdraw</c>), as LND drops it from its wallet store. Inputs
    /// leased through walletrpc stay leased until their lease ends or <c>ReleaseOutput</c>; the inputs of a
    /// <c>withdraw</c>/<c>SendOutputs</c> reservation are released by the withdraw orphan rule once no pending row spends
    /// them, so they can be spent again (as LND frees a removed transaction's inputs) while the removed transaction may
    /// still sit in mempools. Only a spend whose wallet inputs are all held by walletrpc leases or withdraw reservations
    /// qualifies: the node's own transactions (fundings, sweeps, commitments, the anchor CPFP reclaim stored as a wallet
    /// send, ...) are never removed, and a confirmed transaction cannot be.
    /// </summary>
    public override async Task<RemoveTransactionResponse> RemoveTransaction(GetTransactionRequest request,
                                                                           ServerCallContext context)
    {
        var txId = ParseTxId(request.Txid);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var row = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId);
        if (row is null || row.State is BroadcastState.Abandoned or BroadcastState.Replaced)
            throw new RpcException(new Status(StatusCode.NotFound,
                                              $"transaction with txid={request.Txid} not found in the internal "
                                            + "wallet store"));
        if (row.State == BroadcastState.Confirmed)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                              $"transaction with txid={request.Txid} is already confirmed"));
        if (row.Purpose is not (BroadcastPurpose.WalletSend or BroadcastPurpose.WalletCollaborative))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                              $"transaction with txid={request.Txid} is this node's own "
                                            + $"{row.Purpose} transaction; only wallet sends can be removed"));
        if (row.Purpose == BroadcastPurpose.WalletSend && !await IsOperatorSpendAsync(unitOfWork, row))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                              $"transaction with txid={request.Txid} is this node's own wallet "
                                            + "transaction (not a walletrpc or withdraw spend); it cannot be removed"));

        await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(txId);
        await unitOfWork.SaveChangesAsync();
        return new RemoveTransactionResponse { Status = "Successfully removed transaction" };
    }

    /// <summary>
    /// Whether a <see cref="BroadcastPurpose.WalletSend"/> row is an operator's spend (published through walletrpc or
    /// <c>withdraw</c>/<c>SendOutputs</c>): its inputs are held by reservations, every one a walletrpc lease or a
    /// withdraw reservation. The node's own wallet sends (the anchor CPFP reclaim, which must replace stuck children)
    /// spend inputs of other reservations.
    /// </summary>
    private static async Task<bool> IsOperatorSpendAsync(IUnitOfWork unitOfWork, BroadcastTransactionModel row)
    {
        NBitcoin.Transaction tx;
        try
        {
            tx = NBitcoin.Transaction.Load(row.RawTransaction, Network.Main);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or EndOfStreamException)
        {
            return false;
        }

        var spent = tx.Inputs.Select(i => (new TxId(i.PrevOut.Hash.ToBytes()), i.PrevOut.N)).ToHashSet();
        var holding = (await unitOfWork.FeeInputReservationDbRepository.GetAllAsync())
                     .Where(r => r.Inputs.Any(i => spent.Contains((i.TxId, i.Index))))
                     .ToList();
        return holding.Count > 0
            && holding.All(r => r.Purpose == WalletSpendService.ReservationPurpose
                             || r.Purpose.StartsWith(WalletPsbtService.LeasePurposePrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>RequiredReserve</c>: the on-chain reserve of the node's anchors channels (open, and being opened) plus
    /// <c>additional_public_channels</c> more, as <c>Node:Anchors</c> computes it (per channel, capped), which is the
    /// reserve the node's own spends and channel fundings keep.
    /// </summary>
    public override Task<RequiredReserveResponse> RequiredReserve(RequiredReserveRequest request,
                                                                 ServerCallContext context)
    {
        var reserve = _serviceProvider.GetService<IAnchorReserveService>()
                   ?? throw new RpcException(new Status(StatusCode.Unavailable,
                                                        "this node has no anchors reserve service"));
        var additional = (int)Math.Min(request.AdditionalPublicChannels, int.MaxValue);
        return Task.FromResult(new RequiredReserveResponse
        {
            RequiredReserve = reserve.GetRequiredReserve(additional).Satoshi
        });
    }

    /// <summary>
    /// <c>SubmitPackage</c>: bitcoind's <c>submitpackage</c> of the transactions as given (parents first, the child
    /// last), <c>sat_per_vbyte</c> as its <c>maxfeerate</c> (0 = no limit, unset = bitcoind's default), answered with
    /// bitcoind's package message, its per-wtxid results and the transactions package RBF replaced. Nothing is stored:
    /// the transactions are not rebroadcast, and the chain monitor books what they move in the wallet when they confirm.
    /// </summary>
    public override async Task<SubmitPackageResponse> SubmitPackage(SubmitPackageRequest request,
                                                                   ServerCallContext context)
    {
        if (request.RawTxs.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "no transactions to submit"));

        var transactions = new List<NBitcoin.Transaction>();
        foreach (var raw in request.RawTxs)
        {
            try
            {
                transactions.Add(NBitcoin.Transaction.Load(raw.ToByteArray(), _network));
            }
            catch (Exception e) when (e is FormatException or ArgumentException or EndOfStreamException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                  $"transaction {transactions.Count} does not parse: {e.Message}"));
            }
        }

        var chain = _serviceProvider.GetService<IBitcoinChainService>()
                 ?? throw new RpcException(new Status(StatusCode.Unavailable, "this node has no chain backend"));
        decimal? maxFeeRate = request.HasSatPerVbyte ? request.SatPerVbyte / 100_000m : null;
        Infrastructure.Bitcoin.Wallet.Models.RawPackageSubmitResult result;
        try
        {
            result = await chain.SubmitRawPackageAsync(transactions, maxFeeRate);
        }
        catch (NotSupportedException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }
        catch (NBitcoin.RPC.RPCException e)
        {
            throw new RpcException(new Status(StatusCode.Unknown, e.Message));
        }

        var response = new SubmitPackageResponse { PackageMsg = result.PackageMessage };
        foreach (var transaction in result.Transactions)
            response.TxResults[transaction.Wtxid] = new SubmitPackageTxResult
            {
                Txid = transaction.Txid,
                Error = transaction.Error ?? "",
                OtherWtxid = transaction.OtherWtxid ?? ""
            };
        response.ReplacedTransactions.AddRange(result.ReplacedTransactions);
        return response;
    }

    /// <summary>
    /// <c>SignMessageWithAddr</c>: Bitcoin Core's compact message signature (base64) with the key of one of the
    /// wallet's addresses (P2WPKH, or P2TR with its untweaked internal key, as LND), signed inside the node's signer.
    /// An address the wallet did not derive is <c>NOT_FOUND</c>.
    /// </summary>
    public override async Task<SignMessageWithAddrResponse> SignMessageWithAddr(SignMessageWithAddrRequest request,
                                                                               ServerCallContext context)
    {
        var address = ParseAddressString(request.Addr);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var wanted = address.ToString();
        var walletAddress = unitOfWork.WalletAddressesDbRepository.GetAllAddresses()
                                      .FirstOrDefault(a => string.Equals(a.Address, wanted,
                                                                         StringComparison.OrdinalIgnoreCase))
                         ?? throw new RpcException(new Status(StatusCode.NotFound,
                                                              "address could not be found in the wallet database"));
        var signer = _serviceProvider.GetService<ILightningSigner>()
                  ?? throw new RpcException(new Status(StatusCode.Unavailable, "this node has no signer"));
        try
        {
            var signature = signer.SignWalletMessage(walletAddress, request.Msg.ToByteArray());
            return new SignMessageWithAddrResponse { Signature = Convert.ToBase64String(signature) };
        }
        catch (SignerException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }
    }

    /// <summary>
    /// <c>VerifyMessageWithAddr</c>: whether the compact signature (base64) over the message recovers a key that owns
    /// the address (P2PKH, P2WPKH, nested P2WPKH, or P2TR through the key's BIP 86 output key, as LND), and that key;
    /// any address of the network, the wallet's or not.
    /// </summary>
    public override Task<VerifyMessageWithAddrResponse> VerifyMessageWithAddr(VerifyMessageWithAddrRequest request,
                                                                             ServerCallContext context)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(request.Signature);
        }
        catch (FormatException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              $"malformed base64 encoding of the signature: {e.Message}"));
        }

        var address = ParseAddressString(request.Addr);
        (bool Valid, byte[] PubKey)? verified;
        try
        {
            verified = BitcoinMessageSignature.Verify(address, request.Msg.ToByteArray(), signature);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }

        if (verified is not { } result)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "unable to recover public key from compact signature"));

        return Task.FromResult(new VerifyMessageWithAddrResponse
        {
            Valid = result.Valid,
            Pubkey = ByteString.CopyFrom(result.PubKey)
        });
    }

    /// <summary>A label LND and this node's storage both take (LND: 500 characters; here 256 UTF-8 bytes).</summary>
    internal static void CheckLabel(string label)
    {
        if (label.Length > LndLabelLimit)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              $"label length exceeds limit of {LndLabelLimit}"));
        if (SourceLabelRules.ValidateLabel(label) is { } error)
            throw new RpcException(new Status(StatusCode.InvalidArgument, error));
    }

    /// <summary>A txid in display order, as LND's string fields carry it.</summary>
    private static TxId ParseTxId(string txid)
    {
        if (txid.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "must provide a transaction hash"));
        if (!uint256.TryParse(txid, out var hash))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"invalid transaction hash {txid}"));
        return new TxId(hash.ToBytes());
    }

    private BitcoinAddress ParseAddressString(string address)
    {
        try
        {
            return BitcoinAddress.Create(address, _network);
        }
        catch (FormatException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"unable to decode address: {e.Message}"));
        }
    }
}