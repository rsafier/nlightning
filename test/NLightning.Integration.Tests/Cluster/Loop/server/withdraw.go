// Test-owned extension to Loop PR 1222's disposable regtest server. It is
// compiled in a staged copy; the pinned upstream checkout stays untouched.
package server

import (
	"bytes"
	"context"
	"fmt"

	"github.com/btcsuite/btcd/btcec/v2/schnorr/musig2"
	"github.com/btcsuite/btcd/btcutil/psbt"
	"github.com/btcsuite/btcd/txscript"
	"github.com/lightninglabs/lndclient"
	"github.com/lightninglabs/loop/swapserverrpc"
	"github.com/lightningnetwork/lnd/input"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// ServerPsbtWithdrawDeposits supplies real LND MuSig2 partial signatures for a
// cooperative withdrawal. It only accepts confirmed, unspent deposits belonging
// to addresses created by this regtest server, with Core-validated prevouts.
func (s *Server) ServerPsbtWithdrawDeposits(ctx context.Context,
	req *swapserverrpc.ServerPsbtWithdrawRequest) (*swapserverrpc.ServerPsbtWithdrawResponse, error) {
	if req == nil || len(req.WithdrawalPsbt) == 0 {
		return nil, status.Error(codes.InvalidArgument, "withdrawal PSBT is required")
	}
	packet, err := psbt.NewFromRawBytes(bytes.NewReader(req.WithdrawalPsbt), false)
	if err != nil {
		return nil, status.Errorf(codes.InvalidArgument, "withdrawal PSBT: %v", err)
	}
	tx := packet.UnsignedTx
	if len(tx.TxIn) == 0 || len(tx.TxIn) > 100 || len(tx.TxIn) != len(req.DepositToNonces) {
		return nil, status.Error(codes.InvalidArgument, "invalid deposit/nonce count")
	}
	addresses := make([]*staticAddress, len(tx.TxIn))
	prevouts := txscript.NewMultiPrevOutFetcher(nil)
	seen := make(map[string]bool)
	var totalInput int64
	for i, txin := range tx.TxIn {
		outpoint := txin.PreviousOutPoint
		key := outpoint.String()
		if seen[key] || len(req.DepositToNonces[key]) != musig2.PubNonceSize {
			return nil, status.Error(codes.InvalidArgument, "duplicate deposit or invalid nonce")
		}
		seen[key] = true
		s.mu.RLock()
		_, locked := s.lockedUTXOs[key]
		s.mu.RUnlock()
		if locked {
			return nil, status.Errorf(codes.FailedPrecondition, "deposit %s belongs to an active swap", key)
		}
		prevout, confirmations, err := s.fetchStaticDeposit(outpoint)
		if err != nil || confirmations < 1 {
			return nil, status.Errorf(codes.FailedPrecondition, "confirmed unspent deposit %s required: %v", key, err)
		}
		provided := packet.Inputs[i].WitnessUtxo
		if provided == nil || provided.Value != prevout.Value || !bytes.Equal(provided.PkScript, prevout.PkScript) ||
			packet.Inputs[i].SighashType != txscript.SigHashDefault {
			return nil, status.Error(codes.InvalidArgument, "prevout or sighash does not match Core")
		}
		addresses[i] = s.addressForPkScript(prevout.PkScript)
		if addresses[i] == nil {
			return nil, status.Error(codes.NotFound, "unknown static address")
		}
		prevouts.AddPrevOut(outpoint, prevout)
		totalInput += prevout.Value
	}
	var totalOutput int64
	for _, output := range tx.TxOut {
		if output.Value <= 0 || output.Value > totalInput-totalOutput {
			return nil, status.Error(codes.InvalidArgument, "invalid withdrawal output amount")
		}
		totalOutput += output.Value
	}
	if len(tx.TxOut) == 0 || totalOutput >= totalInput {
		return nil, status.Error(codes.InvalidArgument, "withdrawal must pay a positive fee")
	}
	sighashes := txscript.NewTxSigHashes(tx, prevouts)
	txid := tx.TxHash()
	response := &swapserverrpc.ServerPsbtWithdrawResponse{
		Txid: txid.CloneBytes(), SigningInfo: make(map[string]*swapserverrpc.ServerPsbtWithdrawSigningInfo),
	}
	for i, txin := range tx.TxIn {
		address := addresses[i]
		root := address.contract.RootHash
		session, err := s.cfg.Lnd.Signer.MuSig2CreateSession(ctx, input.MuSig2Version100RC2,
			&address.serverKey.locator, [][]byte{address.clientKey.SerializeCompressed(), address.serverKey.pubKey.SerializeCompressed()},
			lndclient.MuSig2TaprootTweakOpt(root[:], false))
		if err != nil {
			return nil, err
		}
		// Also clean up when registration or signing fails. No secret nonces are retained.
		defer s.cfg.Lnd.Signer.MuSig2Cleanup(context.WithoutCancel(ctx), session.SessionID)
		key := txin.PreviousOutPoint.String()
		var nonce [musig2.PubNonceSize]byte
		copy(nonce[:], req.DepositToNonces[key])
		ready, err := s.cfg.Lnd.Signer.MuSig2RegisterNonces(ctx, session.SessionID, [][musig2.PubNonceSize]byte{nonce})
		if err != nil || !ready {
			return nil, fmt.Errorf("withdrawal nonce registration: ready=%v, error=%v", ready, err)
		}
		digestBytes, err := txscript.CalcTaprootSignatureHash(sighashes, txscript.SigHashDefault, tx, i, prevouts)
		if err != nil {
			return nil, err
		}
		var digest [32]byte
		copy(digest[:], digestBytes)
		partial, err := s.cfg.Lnd.Signer.MuSig2Sign(ctx, session.SessionID, digest, true)
		if err != nil {
			return nil, err
		}
		response.SigningInfo[key] = &swapserverrpc.ServerPsbtWithdrawSigningInfo{Nonce: bytes.Clone(session.PublicNonce[:]), Sig: partial}
	}
	return response, nil
}
