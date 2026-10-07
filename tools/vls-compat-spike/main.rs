//! Research-only exercise of pinned VLS public APIs. Fixed seeds are test fixtures.
use bitcoin::bip32::{DerivationPath, Xpriv};
use bitcoin::hashes::Hash;
use bitcoin::secp256k1::{Message, PublicKey, Secp256k1, SecretKey};
use bitcoin::sighash::SighashCache;
use bitcoin::{Amount, EcdsaSighashType, Network, OutPoint, Txid};
use lightning::ln::chan_utils::{
    build_commitment_secret, make_funding_redeemscript, ChannelPublicKeys, CommitmentTransaction,
};
use lightning::types::payment::PaymentHash;
use lightning_signer::channel::{ChannelBase, ChannelSetup, CommitmentType};
use lightning_signer::node::{Node, NodeConfig, NodeServices};
use lightning_signer::persist::DummyPersister;
use lightning_signer::policy::simple_validator::{
    make_default_simple_policy, SimpleValidatorFactory,
};
use lightning_signer::signer::{
    derive::{key_derive, KeyDerivationStyle},
    StartingTimeFactory,
};
use lightning_signer::tx::tx::HTLCInfo2;
use lightning_signer::util::{clock::StandardClock, status::Code, INITIAL_COMMITMENT_NUMBER};
use lightning_signer::{bitcoin, lightning};
use std::sync::Arc;

// Test-only entropy makes LDK channel key vectors reproducible. Never use for live signing.
struct FixtureStartingTime;
impl lightning_signer::SendSync for FixtureStartingTime {}
impl StartingTimeFactory for FixtureStartingTime {
    fn starting_time(&self) -> (u64, u32) {
        (1, 1)
    }
}

fn pubkey(n: u8) -> PublicKey {
    PublicKey::from_secret_key(&Secp256k1::new(), &SecretKey::from_slice(&[n; 32]).unwrap())
}
fn node(style: KeyDerivationStyle) -> Arc<Node> {
    node_on(style, Network::Regtest)
}
fn node_on(style: KeyDerivationStyle, network: Network) -> Arc<Node> {
    let mut policy = make_default_simple_policy(network);
    policy.enforce_balance = true;
    Arc::new(Node::new(
        NodeConfig {
            key_derivation_style: style,
            use_checkpoints: false,
            ..NodeConfig::new(network)
        },
        &[42; 32],
        vec![],
        NodeServices {
            validator_factory: Arc::new(SimpleValidatorFactory::new_with_policy(policy)),
            starting_time_factory: Arc::new(FixtureStartingTime),
            persister: Arc::new(DummyPersister {}),
            clock: Arc::new(StandardClock()),
            trusted_oracle_pubkeys: vec![],
        },
    ))
}
fn remote_secret(n: u64) -> SecretKey {
    SecretKey::from_slice(&build_commitment_secret(
        &[55; 32],
        INITIAL_COMMITMENT_NUMBER - n,
    ))
    .unwrap()
}
fn remote_point(n: u64) -> PublicKey {
    PublicKey::from_secret_key(&Secp256k1::new(), &remote_secret(n))
}
fn main() {
    let secp = Secp256k1::new();
    let path: DerivationPath = "m/1017'/0'/6'/0/0".parse().unwrap();
    let nltg_key = Xpriv::new_master(Network::Regtest, &[42; 32])
        .unwrap()
        .derive_priv(&secp, &path)
        .unwrap();
    let nltg_id = PublicKey::from_secret_key(&secp, &nltg_key.private_key);
    println!("NLightning v3 identity: {nltg_id}");
    for style in [
        KeyDerivationStyle::Native,
        KeyDerivationStyle::Ldk,
        KeyDerivationStyle::Lnd,
    ] {
        let id = node(style).get_id();
        assert_ne!(
            id, nltg_id,
            "stock derivation unexpectedly matches NLightning regtest"
        );
        println!("VLS {style} identity: {id} (different from NLightning regtest)");
    }
    let (lnd_mainnet_id, _) =
        key_derive(KeyDerivationStyle::Lnd, Network::Bitcoin).node_keys(&[42; 32], &secp);
    assert_eq!(lnd_mainnet_id, nltg_id);
    println!("VLS lnd mainnet identity matches NLightning v3 node identity only: {lnd_mainnet_id}");
    // Compare all five channel key roles using a real VLS channel stub, not only node IDs.
    let nltg_channel_keys: Vec<_> = [0, 1, 2, 3, 4]
        .iter()
        .map(|role| {
            let path: DerivationPath = format!("m/6425'/0'/0'/0/1/{role}'").parse().unwrap();
            let key = Xpriv::new_master(Network::Regtest, &[42; 32])
                .unwrap()
                .derive_priv(&secp, &path)
                .unwrap();
            PublicKey::from_secret_key(&secp, &key.private_key)
        })
        .collect();
    for network in [Network::Regtest, Network::Bitcoin] {
        for style in [
            KeyDerivationStyle::Native,
            KeyDerivationStyle::Ldk,
            KeyDerivationStyle::Lnd,
        ] {
            let signer = node_on(style, network);
            let (channel_id, _) = signer
                .new_channel(1, &pubkey(9).serialize(), &signer)
                .unwrap();
            let points = signer
                .with_channel_base(&channel_id, |chan| Ok(chan.get_channel_basepoints()))
                .unwrap();
            let stock = [
                points.funding_pubkey,
                points.revocation_basepoint.to_public_key(),
                points.payment_point,
                points.delayed_payment_basepoint.to_public_key(),
                points.htlc_basepoint.to_public_key(),
            ];
            for (role, (ours, theirs)) in ["funding", "revocation", "payment", "delay", "HTLC"]
                .iter()
                .zip(nltg_channel_keys.iter().zip(stock.iter()))
            {
                assert_ne!(
                    ours, theirs,
                    "{style} channel role {role} unexpectedly matches NLightning"
                );
                println!(
                    "Channel {role}: NLightning={ours}, VLS {style} {network}={theirs} (different)"
                );
            }
        }
    }
    for kind in [
        CommitmentType::StaticRemoteKey,
        CommitmentType::AnchorsZeroFeeHtlc,
    ] {
        let signer = node(KeyDerivationStyle::Native);
        assert_eq!(signer.get_id(), node(KeyDerivationStyle::Native).get_id());
        let (id, _) = signer
            .new_channel(1, &pubkey(9).serialize(), &signer)
            .expect("allocate channel");
        let peer_keys = ChannelPublicKeys {
            funding_pubkey: pubkey(1),
            revocation_basepoint: pubkey(2).into(),
            payment_point: pubkey(3),
            delayed_payment_basepoint: pubkey(4).into(),
            htlc_basepoint: pubkey(5).into(),
        };
        let setup = ChannelSetup {
            is_outbound: true,
            channel_value_sat: 3_000_000,
            push_value_msat: 0,
            funding_outpoint: OutPoint {
                txid: Txid::from_slice(&[2; 32]).unwrap(),
                vout: 0,
            },
            holder_selected_contest_delay: 6,
            counterparty_selected_contest_delay: 7,
            holder_shutdown_script: None,
            counterparty_shutdown_script: None,
            counterparty_points: peer_keys.clone(),
            commitment_type: kind,
        };
        signer
            .setup_channel(id.clone(), None, setup, &DerivationPath::master())
            .expect("setup channel");
        let value = if kind == CommitmentType::AnchorsZeroFeeHtlc {
            2_998_340
        } else {
            2_999_000
        };
        // A peer signs our initial commitment; VLS rebuilds it and validates the signature.
        let holder_tx = signer
            .with_channel(&id, |chan| {
                let params = chan.make_channel_parameters();
                let point = chan.get_per_commitment_point(0)?;
                let commitment = CommitmentTransaction::new(
                    INITIAL_COMMITMENT_NUMBER,
                    &point,
                    value,
                    0,
                    253,
                    vec![],
                    &params.as_holder_broadcastable(),
                    &secp,
                );
                let tx = commitment.trust().built_transaction().transaction.clone();
                let funding_key = chan.keys.pubkeys(&secp).funding_pubkey;
                let script = make_funding_redeemscript(&funding_key, &peer_keys.funding_pubkey);
                let hash = SighashCache::new(&tx)
                    .p2wsh_signature_hash(
                        0,
                        &script,
                        Amount::from_sat(3_000_000),
                        EcdsaSighashType::All,
                    )
                    .unwrap();
                let message = Message::from_digest(hash.to_byte_array());
                let bad_sig = secp.sign_ecdsa(&message, &SecretKey::from_slice(&[99; 32]).unwrap());
                let bad = chan
                    .validate_holder_commitment_tx_phase2(
                        0,
                        253,
                        value,
                        0,
                        vec![],
                        vec![],
                        &bad_sig,
                        &[],
                    )
                    .unwrap_err();
                assert_eq!(chan.enforcement_state.next_holder_commit_num, 0);
                println!("{kind:?}: invalid holder commitment signature refused: {bad}");
                let sig = secp.sign_ecdsa(&message, &SecretKey::from_slice(&[1; 32]).unwrap());
                chan.validate_holder_commitment_tx_phase2(
                    0,
                    253,
                    value,
                    0,
                    vec![],
                    vec![],
                    &sig,
                    &[],
                )?;
                chan.activate_initial_commitment()?;
                println!("{kind:?}: peer-signed holder commitment validated and activated");
                Ok((tx, message, funding_key))
            })
            .unwrap();
        // Initial and next commitments are signed through normal state progression.
        for n in 0..=1 {
            signer.with_channel(&id, |chan| {
                let point = remote_point(n);
                let (sig, htlc_sigs) = chan.sign_counterparty_commitment_tx_phase2(
                    &point, n, 253, value, 0, vec![], vec![])?;
                assert!(htlc_sigs.is_empty());
                let commitment = chan.make_counterparty_commitment_tx(&point, n, 253, value, 0, vec![]);
                let tx = &commitment.trust().built_transaction().transaction;
                let funding_key = chan.keys.pubkeys(&secp).funding_pubkey;
                let script = make_funding_redeemscript(&funding_key, &peer_keys.funding_pubkey);
                let hash = SighashCache::new(tx).p2wsh_signature_hash(
                    0, &script, Amount::from_sat(3_000_000), EcdsaSighashType::All).unwrap();
                secp.verify_ecdsa(&Message::from_digest(hash.to_byte_array()), &sig, &funding_key).unwrap();
                // Identical commitment retry is deterministic and does not advance twice.
                let (retry, _) = chan.sign_counterparty_commitment_tx_phase2(
                    &point, n, 253, value, 0, vec![], vec![])?;
                assert_eq!(sig, retry);
                println!("{kind:?}: commitment {n} signature independently verified; identical retry accepted");
                Ok(())
            }).unwrap();
        }
        signer
            .with_channel(&id, |chan| {
                let before_commit = chan.enforcement_state.next_counterparty_commit_num;
                let before_revoke = chan.enforcement_state.next_counterparty_revoke_num;
                let changed = chan
                    .sign_counterparty_commitment_tx_phase2(
                        &remote_point(1),
                        1,
                        253,
                        value - 100_000,
                        100_000,
                        vec![],
                        vec![],
                    )
                    .unwrap_err();
                assert!(
                    changed.message().contains("retry") || changed.message().contains("balance")
                );
                println!("{kind:?}: conflicting commitment retry refused: {changed}");
                let skipped = chan
                    .sign_counterparty_commitment_tx_phase2(
                        &remote_point(3),
                        3,
                        253,
                        value,
                        0,
                        vec![],
                        vec![],
                    )
                    .unwrap_err();
                assert_eq!(skipped.code(), Code::FailedPrecondition);
                assert!(skipped.message().contains("next_counterparty_revoke_num"));
                println!("{kind:?}: skipped commitment refused: {skipped}");
                let bad_secret = chan
                    .validate_counterparty_revocation(0, &SecretKey::from_slice(&[99; 32]).unwrap())
                    .unwrap_err();
                assert_eq!(bad_secret.code(), Code::FailedPrecondition);
                assert!(bad_secret
                    .message()
                    .contains("revocation commit point mismatch"));
                println!("{kind:?}: invalid revocation secret refused: {bad_secret}");
                assert_eq!(
                    chan.enforcement_state.next_counterparty_commit_num,
                    before_commit
                );
                assert_eq!(
                    chan.enforcement_state.next_counterparty_revoke_num,
                    before_revoke
                );
                chan.validate_counterparty_revocation(0, &remote_secret(0))?;
                assert_eq!(chan.enforcement_state.next_counterparty_revoke_num, 1);
                chan.validate_counterparty_revocation(0, &remote_secret(0))?;
                chan.sign_counterparty_commitment_tx_phase2(
                    &remote_point(2),
                    2,
                    253,
                    value,
                    0,
                    vec![],
                    vec![],
                )?;
                println!("{kind:?}: correct revocation and retry accepted; commitment 2 signed");
                Ok(())
            })
            .unwrap();
        signer
            .with_channel(&id, |chan| {
                chan.validate_counterparty_revocation(1, &remote_secret(1))?;
                let htlc = HTLCInfo2 {
                    value_sat: 100_000,
                    payment_hash: PaymentHash([77; 32]),
                    cltv_expiry: 100,
                };
                let unauthorized = chan
                    .sign_counterparty_commitment_tx_phase2(
                        &remote_point(3),
                        3,
                        253,
                        value - 100_000,
                        0,
                        vec![],
                        vec![htlc.clone()],
                    )
                    .unwrap_err();
                assert_eq!(chan.enforcement_state.next_counterparty_commit_num, 3);
                assert_eq!(unauthorized.code(), Code::FailedPrecondition);
                assert!(unauthorized.message().contains("unbalanced payments"));
                println!("{kind:?}: unauthorized outgoing HTLC refused: {unauthorized}");
                Ok(())
            })
            .unwrap();
        signer
            .add_keysend(pubkey(9), PaymentHash([77; 32]), 100_000_000)
            .unwrap();
        signer
            .with_channel(&id, |chan| {
                let htlc = HTLCInfo2 {
                    value_sat: 100_000,
                    payment_hash: PaymentHash([77; 32]),
                    cltv_expiry: 100,
                };
                let (_, sigs) = chan.sign_counterparty_commitment_tx_phase2(
                    &remote_point(3),
                    3,
                    253,
                    value - 100_000,
                    0,
                    vec![],
                    vec![htlc],
                )?;
                assert_eq!(sigs.len(), 1);
                println!(
                    "{kind:?}: explicitly authorized outgoing HTLC signed (one HTLC signature)"
                );
                Ok(())
            })
            .unwrap();
        signer
            .with_channel(&id, |chan| {
                let sig = chan.sign_holder_commitment_tx_phase2(0)?;
                secp.verify_ecdsa(&holder_tx.1, &sig, &holder_tx.2).unwrap();
                println!("{kind:?}: holder force-close signature independently verified");
                Ok(())
            })
            .unwrap();
    }
    println!("PASS: pinned VLS regtest core compatibility spike");
}
