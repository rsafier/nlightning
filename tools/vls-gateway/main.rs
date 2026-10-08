//! Experimental local regtest gateway for the NLightning VLS semantic adapter.
use lightning_signer::bitcoin::{
    bech32::Fe32,
    bip32::DerivationPath,
    consensus::deserialize,
    hashes::{sha256, Hash},
    secp256k1::Secp256k1,
    secp256k1::{ecdsa::Signature, PublicKey, SecretKey},
    Network, ScriptBuf, Transaction, TxOut,
};
use lightning_signer::channel::{ChannelId, ChannelSetup, CommitmentType};
use lightning_signer::invoice::{
    bolt11::{Bolt11Invoice, RawBolt11Invoice},
    Invoice,
};
use lightning_signer::lightning::types::payment::{PaymentHash, PaymentPreimage};
use lightning_signer::node::{Node, NodeConfig, NodeServices};
use lightning_signer::persist::Persist;
use lightning_signer::policy::simple_validator::{
    make_default_simple_policy, SimpleValidatorFactory,
};
use lightning_signer::signer::ClockStartingTimeFactory;
use lightning_signer::tx::tx::HTLCInfo2;
use lightning_signer::util::clock::StandardClock;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs,
    io::{BufRead, BufReader, Read, Write},
    os::unix::{
        fs::PermissionsExt,
        net::{UnixListener, UnixStream},
    },
    path::Path,
    str::FromStr,
    sync::Arc,
};
use vls_persist::kvv::{
    redb::RedbKVVStore, transactional::TransactionalKVVStore, JsonFormat, KVVPersister, KVVStore,
};
use zeroize::Zeroize;

type Store = KVVPersister<TransactionalKVVStore<RedbKVVStore>, JsonFormat>;
const PREFIX: &str = "nltg-gateway/receipt/";
const MAX_RECEIPTS: usize = 65536;
const MAX_FRAME: usize = 1024 * 1024;
const MAX_RECEIPT_BYTES: usize = 64 * 1024 * 1024;
const RECEIPT_RESERVE: usize = 4 * 1024 * 1024;
#[derive(Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case", deny_unknown_fields)]
enum Command {
    Identity,
    Ecdh {
        public_key: String,
    },
    SignInvoice {
        hrp: String,
        words: Vec<u8>,
    },
    WalletPublicKey {
        index: u32,
    },
    PublicAccount,
    MarkDataLoss {
        channel: String,
    },
    BroadcastStatus {
        channel: String,
    },
    VerifyBroadcastMark {
        channel: String,
        number: u64,
    },
    SignChannelUpdate {
        payload: String,
    },
    SignNodeAnnouncement {
        payload: String,
    },
    WalletSign {
        transaction: String,
        input_paths: Vec<String>,
        prev_outputs: Vec<TxOut>,
        output_paths: Vec<String>,
    },
    Basepoints {
        channel: String,
    },
    AuthorizeInvoice {
        invoice: String,
    },
    PaymentPreimages {
        channel: String,
        preimages: Vec<String>,
    },
    Allocate {
        peer: String,
        dbid: u64,
    },
    Setup {
        channel: String,
        setup: ChannelSetup,
        #[serde(default)]
        shutdown_path: Option<String>,
    },
    Point {
        channel: String,
        number: u64,
    },
    SignRemote {
        channel: String,
        point: String,
        number: u64,
        feerate: u32,
        holder_sat: u64,
        peer_sat: u64,
        offered: Vec<HTLCInfo2>,
        received: Vec<HTLCInfo2>,
    },
    ValidateHolder {
        channel: String,
        number: u64,
        feerate: u32,
        holder_sat: u64,
        peer_sat: u64,
        offered: Vec<HTLCInfo2>,
        received: Vec<HTLCInfo2>,
        signature: String,
        htlc_signatures: Vec<String>,
    },
    Activate {
        channel: String,
    },
    RevokeHolder {
        channel: String,
        number: u64,
    },
    ValidateRevocation {
        channel: String,
        number: u64,
        secret: String,
        #[serde(default)]
        next_point: Option<String>,
    },
    ForceClose {
        channel: String,
        number: u64,
    },
    MutualClose {
        channel: String,
        holder_sat: u64,
        peer_sat: u64,
        holder_script: Option<String>,
        peer_script: Option<String>,
        #[serde(default)]
        shutdown_path: Option<String>,
    },
    AuthorizeKeysend {
        payee: String,
        hash: String,
        amount_msat: u64,
    },
    Reconcile {
        id: String,
        command: Box<Command>,
    },
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Request {
    token: String,
    id: String,
    command: Command,
}
#[derive(Serialize, Deserialize)]
struct Receipt {
    digest: String,
    result: Value,
}
struct Gateway {
    node: Arc<Node>,
    store: Arc<Store>,
    token: Vec<u8>,
    approval: Vec<u8>,
}
fn key(s: &str) -> Result<PublicKey, String> {
    PublicKey::from_str(s).map_err(|_| "invalid public key".into())
}
fn channel(s: &str) -> Result<ChannelId, String> {
    let b = hex::decode(s).map_err(|_| "invalid channel ID")?;
    if b.len() != 41 {
        return Err("channel ID must be 41 bytes".into());
    }
    PublicKey::from_slice(&b[..33]).map_err(|_| "invalid channel peer")?;
    Ok(ChannelId::new(&b))
}
fn canonical_channel(s: &str) -> Result<String, String> {
    Ok(hex::encode(channel(s)?.as_slice()))
}
fn id(s: &str) -> Result<(), String> {
    if s.is_empty()
        || s.len() > 128
        || !s
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
    {
        Err("invalid request ID".into())
    } else {
        Ok(())
    }
}
fn equal(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.iter().zip(b).fold(0u8, |v, (a, b)| v | (a ^ b)) == 0
}
impl Gateway {
    fn open(
        dir: &Path,
        seed: &[u8; 32],
        token: Vec<u8>,
        approval: Vec<u8>,
    ) -> Result<Self, String> {
        let store = Arc::new(KVVPersister(
            TransactionalKVVStore::new(RedbKVVStore::new(dir)),
            JsonFormat,
        ));
        let mut policy = make_default_simple_policy(Network::Regtest);
        policy.enforce_balance = true;
        let services = NodeServices {
            validator_factory: Arc::new(SimpleValidatorFactory::new_with_policy(policy)),
            starting_time_factory: ClockStartingTimeFactory::new(),
            persister: store.clone(),
            clock: Arc::new(StandardClock()),
            trusted_oracle_pubkeys: vec![],
        };
        let config = NodeConfig {
            use_checkpoints: false,
            ..NodeConfig::new(Network::Regtest)
        };
        let fresh = Arc::new(Node::new(config.clone(), seed, vec![], services.clone()));
        let binding = json!({"version":1,"network":"regtest","derivation":"native","balance":true,"node":fresh.get_id().to_string()});
        let nodes = store.get_nodes().map_err(|e| format!("restore: {e:?}"))?;
        let node = if nodes.is_empty() {
            if store
                .get("nltg-gateway/config")
                .map_err(|e| format!("{e:?}"))?
                .is_some()
            {
                return Err("incomplete signer store".into());
            }
            store.enter().map_err(|e| format!("{e:?}"))?;
            fresh.add_allowlist(&[]).map_err(|e| e.to_string())?;
            store
                .new_node(&fresh.get_id(), &config, &fresh.get_state())
                .map_err(|e| format!("{e:?}"))?;
            store
                .new_tracker(&fresh.get_id(), &fresh.get_tracker())
                .map_err(|e| format!("{e:?}"))?;
            store
                .put("nltg-gateway/config", serde_json::to_vec(&binding).unwrap())
                .map_err(|e| format!("{e:?}"))?;
            store.commit().map_err(|e| format!("{e:?}"))?;
            fresh
        } else {
            if nodes.len() != 1 {
                return Err("one node required".into());
            }
            let saved = store
                .get("nltg-gateway/config")
                .map_err(|e| format!("{e:?}"))?
                .ok_or("missing gateway binding")?;
            if saved.1 != serde_json::to_vec(&binding).unwrap() {
                return Err("seed/network/policy mismatch".into());
            }
            let (node_id, entry) = nodes.into_iter().next().unwrap();
            if entry.network != "regtest"
                || entry.key_derivation_style != 1
                || node_id != fresh.get_id()
            {
                return Err("persisted node configuration mismatch".into());
            }
            Node::restore_node(&node_id, entry, seed, services).map_err(|e| e.to_string())?
        };
        Ok(Self {
            node,
            store,
            token,
            approval,
        })
    }
    fn invalidated(&self, command: &Command) -> Result<bool, String> {
        let normal = matches!(
            command,
            Command::SignRemote { .. }
                | Command::ValidateHolder { .. }
                | Command::Activate { .. }
                | Command::RevokeHolder { .. }
                | Command::MutualClose { .. }
        );
        let ch = match command {
            Command::SignRemote { channel, .. }
            | Command::ValidateHolder { channel, .. }
            | Command::Activate { channel }
            | Command::RevokeHolder { channel, .. }
            | Command::MutualClose { channel, .. }
            | Command::ForceClose { channel, .. } => Some(channel),
            _ => None,
        };
        if let Some(ch) = ch {
            if self
                .store
                .get(&format!(
                    "nltg-gateway/data-loss/{}",
                    canonical_channel(&ch)?
                ))
                .map_err(|e| format!("{e:?}"))?
                .is_some()
            {
                return Ok(true);
            }
            if normal
                && self
                    .store
                    .get(&format!(
                        "nltg-gateway/broadcast/{}",
                        canonical_channel(&ch)?
                    ))
                    .map_err(|e| format!("{e:?}"))?
                    .is_some()
            {
                return Ok(true);
            }
            if normal
                && !matches!(command, Command::MutualClose { .. })
                && self
                    .node
                    .with_channel(&channel(ch)?, |c| Ok(c.enforcement_state.channel_closed))
                    .map_err(|e| e.to_string())?
            {
                return Ok(true);
            }
        }
        Ok(false)
    }
    fn execute(&self, req: Request, admin: bool) -> Result<Value, String> {
        if !equal(
            req.token.as_bytes(),
            if admin { &self.approval } else { &self.token },
        ) {
            return Err("unauthenticated".into());
        }
        id(&req.id)?;
        if let Command::Reconcile {
            id: target,
            command,
        } = &req.command
        {
            id(target)?;
            if matches!(**command, Command::Reconcile { .. }) {
                return Err("nested reconciliation".into());
            }
            if admin
                != matches!(
                    **command,
                    Command::AuthorizeKeysend { .. } | Command::AuthorizeInvoice { .. }
                )
            {
                return Err("operation forbidden on this socket".into());
            }
            if self.invalidated(command)? {
                return Ok(json!({"status":"invalidated"}));
            }
            let digest = sha256::Hash::hash(&serde_json::to_vec(command).unwrap()).to_string();
            return match self.receipt(target)? {
                Some(r) if r.digest == digest => {
                    Ok(json!({"status":"completed","result":r.result}))
                }
                Some(_) => Err("request ID payload mismatch".into()),
                None => Ok(json!({"status":"not_found"})),
            };
        }
        if admin
            != matches!(
                req.command,
                Command::AuthorizeKeysend { .. } | Command::AuthorizeInvoice { .. }
            )
        {
            return Err("operation forbidden on this socket".into());
        }
        if self.invalidated(&req.command)? {
            return Err("signing request invalidated by channel safety state".into());
        }
        let digest = sha256::Hash::hash(&serde_json::to_vec(&req.command).unwrap()).to_string();
        if let Some(r) = self.receipt(&req.id)? {
            return if r.digest == digest {
                Ok(r.result)
            } else {
                Err("request ID payload mismatch".into())
            };
        }
        let receipts = self
            .store
            .get_prefix(PREFIX)
            .map_err(|e| format!("{e:?}"))?
            .collect::<Vec<_>>();
        if receipts.len() >= MAX_RECEIPTS
            || receipts.iter().map(|r| r.1 .1.len()).sum::<usize>()
                > MAX_RECEIPT_BYTES - RECEIPT_RESERVE
        {
            return Err("receipt capacity exhausted".into());
        }
        self.store.enter().map_err(|e| format!("{e:?}"))?;
        let result = self.dispatch(req.command);
        match result {
            Ok(result) => {
                self.store
                    .update_node(&self.node.get_id(), &*self.node.get_state())
                    .expect("node policy state persistence failed");
                let receipt = Receipt {
                    digest,
                    result: result.clone(),
                };
                let bytes = serde_json::to_vec(&receipt).unwrap();
                assert!(
                    bytes.len() <= RECEIPT_RESERVE,
                    "response exceeded reserved receipt capacity; fail stop"
                );
                self.store
                    .put(&format!("{PREFIX}{}", req.id), bytes)
                    .expect("receipt persistence failed");
                self.store
                    .commit()
                    .expect("signer transaction persistence failed");
                Ok(result)
            }
            Err(e) => {
                assert!(
                    self.store.prepare().is_empty(),
                    "policy error left state mutations; fail stop"
                );
                self.store
                    .commit()
                    .expect("failed to clear empty transaction");
                Err(e)
            }
        }
    }
    fn receipt(&self, id: &str) -> Result<Option<Receipt>, String> {
        self.store
            .get(&format!("{PREFIX}{id}"))
            .map_err(|e| format!("{e:?}"))?
            .map(|(_, bytes)| serde_json::from_slice(&bytes).map_err(|_| "invalid receipt".into()))
            .transpose()
    }
    fn dispatch(&self, command: Command) -> Result<Value, String> {
        let guarded = match &command {
            Command::SignRemote { channel, .. }
            | Command::ValidateHolder { channel, .. }
            | Command::Activate { channel }
            | Command::RevokeHolder { channel, .. }
            | Command::ForceClose { channel, .. }
            | Command::MutualClose { channel, .. } => Some(channel),
            _ => None,
        };
        if let Some(ch) = guarded {
            if self
                .store
                .get(&format!(
                    "nltg-gateway/data-loss/{}",
                    canonical_channel(&ch)?
                ))
                .map_err(|e| format!("{e:?}"))?
                .is_some()
            {
                return Err("channel data loss detected".into());
            }
        }
        match command {
            Command::MarkDataLoss { channel:ch } => {
                self.node.with_channel_base(&channel(&ch)?, |_|Ok(())).map_err(|e|e.to_string())?;
                self.store.put(&format!("nltg-gateway/data-loss/{}",canonical_channel(&ch)?),vec![1]).map_err(|e|format!("{e:?}"))?;Ok(json!({}))
            },
            Command::BroadcastStatus { channel:ch } => {
                let value=self.store.get(&format!("nltg-gateway/broadcast/{}",canonical_channel(&ch)?)).map_err(|e|format!("{e:?}"))?;
                Ok(json!({"number":value.map(|(_,b)|serde_json::from_slice::<u64>(&b).expect("corrupt broadcast mark"))}))
            },
            Command::VerifyBroadcastMark { channel:ch, number } => {
                let saved=self.store.get(&format!("nltg-gateway/broadcast/{}",canonical_channel(&ch)?)).map_err(|e|format!("{e:?}"))?.ok_or("VLS broadcast signature was not produced")?;
                if serde_json::from_slice::<u64>(&saved.1).map_err(|_|"corrupt broadcast mark")?!=number {return Err("broadcast mark number mismatch".into());}
                Ok(json!({}))
            },
            Command::SignChannelUpdate { payload } => {
                use lightning_signer::lightning::util::ser::LengthReadable;
                let bytes=hex::decode(payload).map_err(|_|"invalid gossip hex")?;
                let mut input=bytes.as_slice();
                let update:lightning_signer::lightning::ln::msgs::UnsignedChannelUpdate=LengthReadable::read_from_fixed_length_buffer(&mut input).map_err(|_|"invalid channel update")?;
                if !input.is_empty() {return Err("trailing channel update bytes".into());}
                if update.chain_hash!=lightning_signer::bitcoin::constants::ChainHash::using_genesis_block(Network::Regtest) {return Err("channel update chain mismatch".into());}
                Ok(json!({"signature":hex::encode(self.node.sign_channel_update(&bytes).map_err(|e|e.to_string())?.serialize_compact())}))
            },
            Command::SignNodeAnnouncement { payload } => {
                use lightning_signer::lightning::util::ser::LengthReadable;
                let bytes=hex::decode(payload).map_err(|_|"invalid gossip hex")?;
                let mut input=bytes.as_slice();
                let announcement:lightning_signer::lightning::ln::msgs::UnsignedNodeAnnouncement=LengthReadable::read_from_fixed_length_buffer(&mut input).map_err(|_|"invalid node announcement")?;
                if !input.is_empty() || announcement.node_id!=self.node.get_id().into() {return Err("node announcement identity or length mismatch".into());}
                Ok(json!({"signature":hex::encode(self.node.sign_node_announcement(&bytes).map_err(|e|e.to_string())?.serialize_compact())}))
            },
            Command::Ecdh { public_key } => Ok(json!({"secret":hex::encode(self.node.ecdh(&key(&public_key)?))})),
            Command::PublicAccount => Ok(json!({"xpub":self.node.get_account_extended_pubkey().to_string(),"path":"m/0/0"})),
            Command::WalletPublicKey { index } => {
                let path=DerivationPath::from_str(&format!("m/{index}")).map_err(|_|"invalid wallet index")?;
                let xpub=self.node.get_account_extended_pubkey().derive_pub(&Secp256k1::verification_only(), &path).map_err(|_|"invalid wallet derivation")?;
                Ok(json!({"public_key":xpub.public_key.to_string()}))
            },
            Command::SignInvoice { hrp, words } => {
                if !hrp.starts_with("lnbcrt") {return Err("invoice network must be regtest".into());}
                let data=words.into_iter().map(|b| Fe32::try_from(b).map_err(|_|"invalid five-bit invoice word")).collect::<Result<Vec<_>,_>>()?;
                let invoice=RawBolt11Invoice::from_raw(&hrp,&data).map_err(|_|"invalid BOLT11 invoice")?;
                let (rid,sig)=self.node.sign_bolt11_invoice(invoice).map_err(|e|e.to_string())?.serialize_compact();
                let mut bytes=sig.to_vec();bytes.push(rid.to_i32() as u8);Ok(json!({"signature":hex::encode(bytes)}))
            },
            Command::AuthorizeInvoice { invoice } => {
                let parsed=invoice.parse::<Bolt11Invoice>().map_err(|_|"invalid BOLT11 invoice")?;
                if parsed.amount_milli_satoshis().unwrap_or(0)==0 {return Err("positive invoice amount required".into());}
                if parsed.currency()!=lightning_signer::invoice::bolt11::Currency::Regtest {return Err("invoice network must be regtest".into());}
                let added=self.node.add_invoice(Invoice::Bolt11(parsed)).map_err(|e|e.to_string())?;
                Ok(json!({"added":added}))
            },
            Command::PaymentPreimages { channel:ch, preimages } => {
                let count=preimages.len();
                let values=preimages.into_iter().map(|s|hex::decode(s).map_err(|_|"invalid preimage").and_then(|b|b.try_into().map(PaymentPreimage).map_err(|_|"invalid preimage"))).collect::<Result<Vec<_>,_>>()?;
                self.node.with_channel(&channel(&ch)?, |c| {c.htlcs_fulfilled(values);Ok(())}).map_err(|e|e.to_string())?;
                self.node.get_persister().update_node(&self.node.get_id(), &*self.node.get_state()).map_err(|e|format!("preimage persistence: {e:?}"))?;
                Ok(json!({"recorded":count}))
            },
            Command::WalletSign { transaction, input_paths, prev_outputs, output_paths } => {
                let tx:Transaction=deserialize(&hex::decode(transaction).map_err(|_|"invalid transaction hex")?).map_err(|_|"invalid transaction")?;
                if tx.input.len()!=input_paths.len() || tx.input.len()!=prev_outputs.len() || tx.output.len()!=output_paths.len() {return Err("wallet context length mismatch".into());}
                let parse=|p:String|DerivationPath::from_str(&p).map_err(|_|"invalid wallet path");
                let ip=input_paths.into_iter().map(parse).collect::<Result<Vec<_>,_>>()?;
                let op=output_paths.into_iter().map(parse).collect::<Result<Vec<_>,_>>()?;
                if ip.iter().any(|p|p.len()!=1) || prev_outputs.iter().any(|o|!o.script_pubkey.is_p2wpkh()) {return Err("only native P2WPKH wallet inputs supported".into());}
                for (path,prev) in ip.iter().zip(&prev_outputs) {
                    let pubkey=self.node.get_account_extended_pubkey().derive_pub(&Secp256k1::verification_only(),path).map_err(|_|"invalid wallet derivation")?.public_key;
                    let compressed=lightning_signer::bitcoin::CompressedPublicKey(pubkey);
                    let expected=lightning_signer::bitcoin::Address::p2wpkh(&compressed,Network::Regtest).script_pubkey();
                    if prev.script_pubkey!=expected {return Err("wallet input script mismatch".into());}
                }
                let uc=vec![None;tx.input.len()];
                self.node.check_onchain_tx(&tx,&vec![true;tx.input.len()],&prev_outputs,&uc,&op).map_err(|e|e.to_string())?;
                let witnesses=self.node.unchecked_sign_onchain_tx(&tx,&ip,&prev_outputs,uc).map_err(|e|e.to_string())?;
                Ok(json!({"witnesses":witnesses.into_iter().map(|w|w.into_iter().map(hex::encode).collect::<Vec<_>>()).collect::<Vec<_>>()}))
            },
            Command::Basepoints { channel:ch } => self.node.with_channel_base(&channel(&ch)?, |c| {
                let p=c.get_channel_basepoints();Ok(json!({"channel":ch,"funding":p.funding_pubkey.to_string(),"revocation":p.revocation_basepoint.to_public_key().to_string(),"payment":p.payment_point.to_string(),"delay":p.delayed_payment_basepoint.to_public_key().to_string(),"htlc":p.htlc_basepoint.to_public_key().to_string()}))
            }).map_err(|e|e.to_string()),
            Command::Identity => Ok(json!({"node_id":self.node.get_id().to_string(),"network":"regtest","derivation":"native"})),
            Command::Allocate { peer, dbid } => {
                if dbid == 0 { return Err("dbid must be positive".into()); }
                let (id,_) = self.node.new_channel(dbid, &key(&peer)?.serialize(), &self.node).map_err(|e|e.to_string())?;
                self.node.with_channel_base(&id, |c| {
                    let p = c.get_channel_basepoints();
                    Ok(json!({"channel":hex::encode(id.as_slice()),"funding":p.funding_pubkey.to_string(),"revocation":p.revocation_basepoint.to_public_key().to_string(),"payment":p.payment_point.to_string(),"delay":p.delayed_payment_basepoint.to_public_key().to_string(),"htlc":p.htlc_basepoint.to_public_key().to_string()}))
                }).map_err(|e|e.to_string())
            },
            Command::Setup { channel: ch, mut setup, shutdown_path } => {
                if !matches!(setup.commitment_type, CommitmentType::StaticRemoteKey | CommitmentType::AnchorsZeroFeeHtlc) { return Err("unsupported channel type".into()); }
                // Initial push is owned by the existing policy state; host balances change after payments.
                if let Ok(existing_push)=self.node.with_channel(&channel(&ch)?, |c|Ok(c.setup.push_value_msat)) { setup.push_value_msat=existing_push; }
                self.node.setup_channel(channel(&ch)?, None, setup, &shutdown_path.map(|p|DerivationPath::from_str(&p).map_err(|_|"invalid shutdown path")).transpose()?.unwrap_or(DerivationPath::master())).map_err(|e|e.to_string())?; Ok(json!({}))
            },
            Command::Point { channel: ch, number } => self.node.with_channel_base(&channel(&ch)?, |c| Ok(json!({"point":c.get_per_commitment_point(number)?.to_string()}))).map_err(|e|e.to_string()),
            Command::SignRemote { channel: ch, point, number, feerate, holder_sat, peer_sat, offered, received } => {
                let p = key(&point)?;
                self.node.with_channel(&channel(&ch)?, |c| {
                    let (sig, htlcs) = c.sign_counterparty_commitment_tx_phase2(&p, number, feerate, holder_sat, peer_sat, offered, received)?;
                    Ok(json!({"signature":hex::encode(sig.serialize_compact()),"htlc_signatures":htlcs.into_iter().map(|s|hex::encode(s.serialize_compact())).collect::<Vec<_>>()}))
                }).map_err(|e|e.to_string())
            },
            Command::ValidateHolder { channel: ch, number, feerate, holder_sat, peer_sat, offered, received, signature, htlc_signatures } => {
                let parse = |s:&str| -> Result<Signature,String> { Signature::from_compact(&hex::decode(s).map_err(|_|"invalid signature")?).map_err(|_|"invalid signature".into()) };
                if htlc_signatures.len()!=offered.len()+received.len() {return Err("HTLC signature count mismatch".into());}
                let sig = parse(&signature)?; let htlcs = htlc_signatures.iter().map(|s|parse(s)).collect::<Result<Vec<_>,_>>()?;
                self.node.with_channel(&channel(&ch)?, |c| { c.validate_holder_commitment_tx_phase2(number, feerate, holder_sat, peer_sat, offered, received, &sig, &htlcs)?; Ok(json!({})) }).map_err(|e|e.to_string())
            },
            Command::Activate { channel: ch } => self.node.with_channel(&channel(&ch)?, |c| {c.activate_initial_commitment()?;Ok(json!({}))}).map_err(|e|e.to_string()),
            Command::RevokeHolder { channel: ch, number } => self.node.with_channel(&channel(&ch)?, |c| {let(p,s)=c.revoke_previous_holder_commitment(number)?;Ok(json!({"point":p.to_string(),"secret":s.map(|s|hex::encode(s.secret_bytes()))}))}).map_err(|e|e.to_string()),
            Command::ValidateRevocation { channel: ch, number, secret, next_point } => {
                let s=SecretKey::from_str(&secret).map_err(|_|"invalid secret")?;
                if let Some(point)=next_point {let _=key(&point)?;}
                self.node.with_channel(&channel(&ch)?, |c| {c.validate_counterparty_revocation(number,&s)?;Ok(json!({}))}).map_err(|e|e.to_string())
            },
            Command::ForceClose { channel:ch, number } => {
                let mark=format!("nltg-gateway/broadcast/{}",canonical_channel(&ch)?);
                if let Some((_,bytes))=self.store.get(&mark).map_err(|e|format!("{e:?}"))? {
                    if serde_json::from_slice::<u64>(&bytes).map_err(|_|"corrupt broadcast mark")?!=number {return Err("another commitment is signed for broadcast".into());}
                }
                let result=self.node.with_channel(&channel(&ch)?, |c|Ok(json!({"signature":hex::encode(c.sign_holder_commitment_tx_phase2(number)?.serialize_compact())}))).map_err(|e|e.to_string())?;
                self.store.put(&mark,serde_json::to_vec(&number).unwrap()).expect("broadcast mark persistence failed");Ok(result)
            },
            Command::MutualClose { channel: ch, holder_sat, peer_sat, holder_script, peer_script, shutdown_path } => {
                let parse=|s:Option<String>|s.map(|s|hex::decode(s).map(ScriptBuf::from_bytes).map_err(|_|"invalid script")).transpose();
                let hs=parse(holder_script)?; let ps=parse(peer_script)?;
                let path=shutdown_path.map(|p|DerivationPath::from_str(&p).map_err(|_|"invalid shutdown path")).transpose()?.unwrap_or(DerivationPath::master());
                self.node.with_channel(&channel(&ch)?, |c| Ok(json!({"signature":hex::encode(c.sign_mutual_close_tx_phase2(holder_sat,peer_sat,&hs,&ps,&path)?.serialize_compact())}))).map_err(|e|e.to_string())
            },
            Command::AuthorizeKeysend { payee, hash, amount_msat } => {
                let bytes:[u8;32]=hex::decode(hash).map_err(|_|"invalid payment hash")?.try_into().map_err(|_|"invalid payment hash")?;
                let added=self.node.add_keysend(key(&payee)?,PaymentHash(bytes),amount_msat).map_err(|e|e.to_string())?;Ok(json!({"added":added}))
            },
            Command::Reconcile {..} => unreachable!()
        }
    }
}
fn handle(stream: UnixStream, gateway: &Gateway, admin: bool) -> std::io::Result<()> {
    stream.set_read_timeout(Some(std::time::Duration::from_secs(5)))?;
    stream.set_write_timeout(Some(std::time::Duration::from_secs(5)))?;
    let mut line = Vec::new();
    let mut reader = BufReader::new(stream.try_clone()?);
    let count = reader
        .by_ref()
        .take((MAX_FRAME + 1) as u64)
        .read_until(b'\n', &mut line)?;
    let result = if count == 0 || count > MAX_FRAME || line.last() != Some(&b'\n') {
        Err("invalid frame".into())
    } else {
        serde_json::from_slice::<Value>(&line)
            .map_err(|_| "invalid request".into())
            .and_then(|raw| {
                let supplied = raw
                    .get("token")
                    .and_then(Value::as_str)
                    .ok_or("unauthenticated")?;
                if !equal(
                    supplied.as_bytes(),
                    if admin {
                        &gateway.approval
                    } else {
                        &gateway.token
                    },
                ) {
                    return Err("unauthenticated".into());
                }
                fn check_command(command: &Value) -> Result<(), String> {
                    if command.get("op").and_then(Value::as_str) == Some("reconcile") {
                        return check_command(
                            command.get("command").ok_or("missing reconcile command")?,
                        );
                    }
                    if command.get("op").and_then(Value::as_str) == Some("setup") {
                        let txid = command
                            .pointer("/setup/funding_outpoint/txid")
                            .and_then(Value::as_str)
                            .ok_or("invalid setup txid")?;
                        if hex::decode(txid).map_err(|_| "invalid setup txid")?.len() != 32 {
                            return Err("invalid setup txid".into());
                        }
                        if command
                            .pointer("/setup/funding_outpoint/vout")
                            .and_then(Value::as_u64)
                            .ok_or("invalid setup vout")?
                            > u16::MAX as u64
                        {
                            return Err("setup vout exceeds BOLT range".into());
                        }
                    }
                    Ok(())
                }
                check_command(raw.get("command").ok_or("missing command")?)?;
                serde_json::from_value::<Request>(raw)
                    .map_err(|_| "invalid request".into())
                    .and_then(|r| gateway.execute(r, admin))
            })
    };
    let response = match result {
        Ok(result) => json!({"ok":true,"result":result}),
        Err(error) => json!({"ok":false,"error":error}),
    };
    let mut stream = stream;
    serde_json::to_writer(&mut stream, &response)?;
    stream.write_all(b"\n")?;
    stream.flush()
}
fn run() -> Result<(), String> {
    let args: Vec<_> = std::env::args().collect();
    if args.len() != 5 {
        return Err("usage: nlightning-vls-gateway PRIVATE_DIRECTORY NODE_TOKEN_FILE APPROVAL_TOKEN_FILE regtest; inject exactly 32 seed bytes on stdin".into());
    }
    if args[4] != "regtest" {
        return Err("only regtest supported".into());
    }
    let dir = Path::new(&args[1]);
    let md = fs::symlink_metadata(dir).map_err(|_| "private directory must already exist")?;
    if !md.is_dir() || md.permissions().mode() & 0o777 != 0o700 {
        return Err("directory must be private mode 0700".into());
    }
    let token = fs::read(&args[2]).map_err(|_| "node credential unreadable")?;
    let approval = fs::read(&args[3]).map_err(|_| "approval credential unreadable")?;
    if token.len() < 32
        || token.len() > 256
        || approval.len() < 32
        || approval.len() > 256
        || equal(&token, &approval)
    {
        return Err("distinct 32..256-byte credentials required".into());
    }
    if !token.is_ascii() || !approval.is_ascii() {
        return Err("ASCII credentials required".into());
    }
    let mut seed = Vec::new();
    std::io::stdin()
        .take(33)
        .read_to_end(&mut seed)
        .map_err(|_| "seed injection failed")?;
    if seed.len() != 32 {
        seed.zeroize();
        return Err("exactly 32 seed bytes required".into());
    }
    let mut fixed = [0u8; 32];
    fixed.copy_from_slice(&seed);
    seed.zeroize();
    let opened = Gateway::open(dir, &fixed, token, approval);
    fixed.zeroize();
    let gateway = opened?;
    let socket = dir.join("node.sock");
    let admin = dir.join("approval.sock");
    // Redb locks the database before stale socket removal. A second process cannot unlink the owner.
    for path in [&socket, &admin] {
        if path.exists() {
            fs::remove_file(path).map_err(|e| e.to_string())?;
        }
    }
    let listener = UnixListener::bind(&socket).map_err(|e| e.to_string())?;
    let approvals = UnixListener::bind(&admin).map_err(|e| e.to_string())?;
    fs::set_permissions(&socket, fs::Permissions::from_mode(0o600)).map_err(|e| e.to_string())?;
    fs::set_permissions(&admin, fs::Permissions::from_mode(0o600)).map_err(|e| e.to_string())?;
    listener.set_nonblocking(true).map_err(|e| e.to_string())?;
    approvals.set_nonblocking(true).map_err(|e| e.to_string())?;
    loop {
        let mut work = false;
        for (l, a) in [(&listener, false), (&approvals, true)] {
            match l.accept() {
                Ok((s, _)) => {
                    work = true;
                    let _ = handle(s, &gateway, a);
                }
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {}
                Err(e) => return Err(e.to_string()),
            }
        }
        if !work {
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
    }
}
fn main() {
    if let Err(e) = run() {
        eprintln!("gateway refused: {e}");
        std::process::exit(1);
    }
}
