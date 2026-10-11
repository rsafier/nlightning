use lightning_signer::bitcoin::{
    bip32::DerivationPath,
    hashes::{sha256, Hash},
    secp256k1::{ecdsa::Signature, Message, PublicKey, Secp256k1, SecretKey},
    sighash::SighashCache,
    Amount, EcdsaSighashType, Network, OutPoint, Txid,
};
use lightning_signer::channel::{ChannelBase, ChannelSetup, CommitmentType};
use lightning_signer::lightning::ln::chan_utils::{
    build_commitment_secret, make_funding_redeemscript, ChannelPublicKeys, CommitmentTransaction,
};
use lightning_signer::node::{Node, NodeConfig, NodeServices};
use lightning_signer::persist::DummyPersister;
use lightning_signer::policy::simple_validator::{
    make_default_simple_policy, SimpleValidatorFactory,
};
use lightning_signer::signer::ClockStartingTimeFactory;
use lightning_signer::util::clock::StandardClock;
use lightning_signer::util::INITIAL_COMMITMENT_NUMBER;
use serde_json::{json, Value};
use std::sync::Arc;
use std::{
    fs,
    io::{BufRead, BufReader, Write},
    os::unix::{fs::PermissionsExt, net::UnixStream},
    path::PathBuf,
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};
const TOKEN: &str = "11111111111111111111111111111111";
const ADMIN: &str = "22222222222222222222222222222222";
fn pubkey(n: u8) -> PublicKey {
    PublicKey::from_secret_key(&Secp256k1::new(), &SecretKey::from_slice(&[n; 32]).unwrap())
}
fn peer_secret(n: u64) -> SecretKey {
    SecretKey::from_slice(&build_commitment_secret(
        &[55; 32],
        INITIAL_COMMITMENT_NUMBER - n,
    ))
    .unwrap()
}
fn point(n: u64) -> String {
    PublicKey::from_secret_key(&Secp256k1::new(), &peer_secret(n)).to_string()
}
struct Daemon {
    dir: PathBuf,
    child: Option<Child>,
}
impl Daemon {
    fn new() -> Self {
        let dir = std::env::temp_dir().join(format!(
            "nltg-vls-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir(&dir).unwrap();
        fs::set_permissions(&dir, fs::Permissions::from_mode(0o700)).unwrap();
        fs::write(dir.join("token"), TOKEN).unwrap();
        fs::write(dir.join("admin"), ADMIN).unwrap();
        let mut d = Self { dir, child: None };
        d.start(42);
        d
    }
    fn start(&mut self, seed: u8) {
        let mut child = Command::new(env!("CARGO_BIN_EXE_nlightning-vls-gateway"))
            .arg(&self.dir)
            .arg(self.dir.join("token"))
            .arg(self.dir.join("admin"))
            .arg("regtest")
            .stdin(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .unwrap();
        child.stdin.take().unwrap().write_all(&[seed; 32]).unwrap();
        self.child = Some(child);
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            if self.child.as_mut().unwrap().try_wait().unwrap().is_some() {
                panic!("gateway failed startup");
            }
            if UnixStream::connect(self.dir.join("node.sock")).is_ok() {
                break;
            }
            assert!(Instant::now() < deadline, "startup timeout");
            std::thread::sleep(Duration::from_millis(30));
        }
    }
    fn kill(&mut self) {
        if let Some(mut c) = self.child.take() {
            c.kill().unwrap();
            c.wait().unwrap();
        }
    }
    fn raw(&self, id: &str, command: Value, admin: bool, token: &str) -> Value {
        let mut s =
            UnixStream::connect(
                self.dir
                    .join(if admin { "approval.sock" } else { "node.sock" }),
            )
            .unwrap();
        s.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        writeln!(s, "{}", json!({"token":token,"id":id,"command":command})).unwrap();
        let mut line = String::new();
        BufReader::new(s).read_line(&mut line).unwrap();
        serde_json::from_str(&line).unwrap()
    }
    fn call(&self, id: &str, command: Value) -> Value {
        let r = self.raw(id, command, false, TOKEN);
        assert_eq!(r["ok"], true, "{r}");
        r["result"].clone()
    }
}
impl Drop for Daemon {
    fn drop(&mut self) {
        if let Some(mut c) = self.child.take() {
            let _ = c.kill();
            let _ = c.wait();
        }
        let _ = fs::remove_dir_all(&self.dir);
    }
}
fn remote(ch: &str, n: u64) -> Value {
    json!({"op":"sign_remote","channel":ch,"point":point(n),"number":n,"feerate":253,"holder_sat":2999000,"peer_sat":0,"offered":[],"received":[]})
}
#[test]
fn process_restart_atomic_receipts_policy_and_auth() {
    let mut d = Daemon::new();
    assert_eq!(
        d.raw("bad", json!({"op":"identity"}), false, "wrong")["error"],
        "unauthenticated"
    );
    assert_eq!(d.raw("wrong-socket",json!({"op":"authorize_keysend","payee":pubkey(9).to_string(),"hash":"4d".repeat(32),"amount_msat":100000000}),false,TOKEN)["ok"],false);
    let identity = d.call("identity", json!({"op":"identity"}));
    let mut competing = Command::new(env!("CARGO_BIN_EXE_nlightning-vls-gateway"))
        .arg(&d.dir)
        .arg(d.dir.join("token"))
        .arg(d.dir.join("admin"))
        .arg("regtest")
        .stdin(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    competing
        .stdin
        .take()
        .unwrap()
        .write_all(&[42; 32])
        .unwrap();
    assert!(!competing.wait().unwrap().success());
    assert_eq!(d.call("identity", json!({"op":"identity"})), identity);
    let allocated = d.call(
        "allocate",
        json!({"op":"allocate","peer":pubkey(9).to_string(),"dbid":1}),
    );
    let ch = allocated["channel"].as_str().unwrap();
    let setup = ChannelSetup {
        is_outbound: true,
        channel_value_sat: 3000000,
        push_value_msat: 0,
        funding_outpoint: OutPoint {
            txid: Txid::from_slice(&[2; 32]).unwrap(),
            vout: 0,
        },
        holder_selected_contest_delay: 6,
        counterparty_selected_contest_delay: 7,
        holder_shutdown_script: None,
        counterparty_shutdown_script: None,
        counterparty_points: ChannelPublicKeys {
            funding_pubkey: pubkey(1),
            revocation_basepoint: pubkey(2).into(),
            payment_point: pubkey(3),
            delayed_payment_basepoint: pubkey(4).into(),
            htlc_basepoint: pubkey(5).into(),
        },
        commitment_type: CommitmentType::StaticRemoteKey,
    };
    d.call("setup", json!({"op":"setup","channel":ch,"setup":setup}));
    // Independent peer fixture reconstructs the transactions and signs our holder commitment.
    // It is not part of the daemon and does not export keys from it.
    let policy = make_default_simple_policy(Network::Regtest);
    let fixture = Arc::new(Node::new(
        NodeConfig {
            use_checkpoints: false,
            ..NodeConfig::new(Network::Regtest)
        },
        &[42; 32],
        vec![],
        NodeServices {
            validator_factory: Arc::new(SimpleValidatorFactory::new_with_policy(policy)),
            starting_time_factory: ClockStartingTimeFactory::new(),
            persister: Arc::new(DummyPersister {}),
            clock: Arc::new(StandardClock()),
            trusted_oracle_pubkeys: vec![],
        },
    ));
    let (fixture_id, _) = fixture
        .new_channel(1, &pubkey(9).serialize(), &fixture)
        .unwrap();
    fixture
        .setup_channel(
            fixture_id.clone(),
            None,
            setup.clone(),
            &DerivationPath::master(),
        )
        .unwrap();
    let secp = Secp256k1::new();
    let (holder_message, funding_key, holder_signature) = fixture
        .with_channel(&fixture_id, |c| {
            let params = c.make_channel_parameters();
            let p = c.get_per_commitment_point(0)?;
            let tx = CommitmentTransaction::new(
                INITIAL_COMMITMENT_NUMBER,
                &p,
                2999000,
                0,
                253,
                vec![],
                &params.as_holder_broadcastable(),
                &secp,
            )
            .trust()
            .built_transaction()
            .transaction
            .clone();
            let funding = c.keys.pubkeys(&secp).funding_pubkey;
            let script = make_funding_redeemscript(&funding, &pubkey(1));
            let hash = SighashCache::new(&tx)
                .p2wsh_signature_hash(0, &script, Amount::from_sat(3000000), EcdsaSighashType::All)
                .unwrap();
            let msg = Message::from_digest(hash.to_byte_array());
            Ok((
                msg,
                funding,
                secp.sign_ecdsa(&msg, &SecretKey::from_slice(&[1; 32]).unwrap()),
            ))
        })
        .unwrap();
    assert_eq!(funding_key.to_string(), allocated["funding"]);
    d.call("validate-holder-0",json!({"op":"validate_holder","channel":ch,"number":0,"feerate":253,"holder_sat":2999000,"peer_sat":0,"offered":[],"received":[],"signature":hex::encode(holder_signature.serialize_compact()),"htlc_signatures":[]}));
    d.call("activate", json!({"op":"activate","channel":ch}));
    let first = d.call("sign-0", remote(ch, 0));
    fixture
        .with_channel(&fixture_id, |c| {
            let p = PublicKey::from_secret_key(&secp, &peer_secret(0));
            let commitment = c.make_counterparty_commitment_tx(&p, 0, 253, 2999000, 0, vec![]);
            let tx = &commitment.trust().built_transaction().transaction;
            let script = make_funding_redeemscript(&funding_key, &pubkey(1));
            let hash = SighashCache::new(tx)
                .p2wsh_signature_hash(0, &script, Amount::from_sat(3000000), EcdsaSighashType::All)
                .unwrap();
            secp.verify_ecdsa(
                &Message::from_digest(hash.to_byte_array()),
                &Signature::from_compact(
                    &hex::decode(first["signature"].as_str().unwrap()).unwrap(),
                )
                .unwrap(),
                &funding_key,
            )
            .unwrap();
            Ok(())
        })
        .unwrap();
    let command = remote(ch, 1);
    // Send the request but deliberately never consume its original reply.
    let mut unread = UnixStream::connect(d.dir.join("node.sock")).unwrap();
    writeln!(
        unread,
        "{}",
        json!({"token":TOKEN,"id":"sign-1","command":command})
    )
    .unwrap();
    let recovered = d.call(
        "probe",
        json!({"op":"reconcile","id":"sign-1","command":command}),
    );
    assert_eq!(recovered["status"], "completed");
    d.kill();
    drop(unread);
    d.start(42);
    assert_eq!(d.call("identity", json!({"op":"identity"})), identity);
    assert_eq!(d.call("sign-0", remote(ch, 0)), first);
    assert_eq!(
        d.call(
            "probe",
            json!({"op":"reconcile","id":"sign-1","command":command})
        ),
        recovered
    );
    assert_eq!(d.call("sign-1", command.clone()), recovered["result"]);
    let mut changed = command.clone();
    changed["holder_sat"] = json!(2899000);
    changed["peer_sat"] = json!(100000);
    assert_eq!(d.raw("sign-1", changed, false, TOKEN)["ok"], false);
    let before = sha256::Hash::hash(&fs::read(d.dir.join("redb")).unwrap());
    assert_eq!(d.raw("skip", remote(ch, 3), false, TOKEN)["ok"], false);
    assert_eq!(
        sha256::Hash::hash(&fs::read(d.dir.join("redb")).unwrap()),
        before,
        "rejected policy changed durable bytes"
    );
    assert_eq!(
        d.raw(
            "bad-revoke",
            json!({"op":"validate_revocation","channel":ch,"number":0,"secret":"63".repeat(32)}),
            false,
            TOKEN
        )["ok"],
        false
    );
    d.call("revoke-0",json!({"op":"validate_revocation","channel":ch,"number":0,"secret":hex::encode(peer_secret(0).secret_bytes())}));
    d.kill();
    d.start(42);
    d.call("sign-2", remote(ch, 2));
    d.call("revoke-1",json!({"op":"validate_revocation","channel":ch,"number":1,"secret":hex::encode(peer_secret(1).secret_bytes())}));
    let htlc = json!({"value_sat":100000,"payment_hash":vec![77u8;32],"cltv_expiry":100});
    let mut payment = remote(ch, 3);
    payment["holder_sat"] = json!(2899000);
    payment["received"] = json!([htlc]);
    assert_eq!(
        d.raw("unauthorized", payment.clone(), false, TOKEN)["ok"],
        false
    );
    let approved=d.raw("approve",json!({"op":"authorize_keysend","payee":pubkey(9).to_string(),"hash":"4d".repeat(32),"amount_msat":100000000}),true,ADMIN);
    assert_eq!(approved["ok"], true, "{approved}");
    d.kill();
    d.start(42);
    let paid = d.call("payment", payment);
    assert_eq!(paid["htlc_signatures"].as_array().unwrap().len(), 1);
    let closed = d.call(
        "force-close",
        json!({"op":"force_close","channel":ch,"number":0}),
    );
    secp.verify_ecdsa(
        &holder_message,
        &Signature::from_compact(&hex::decode(closed["signature"].as_str().unwrap()).unwrap())
            .unwrap(),
        &funding_key,
    )
    .unwrap();
    // A durable force-close guard invalidates normal-operation receipts, including hex aliases.
    let closed_retry = d.call(
        "force-close",
        json!({"op":"force_close","channel":ch,"number":0}),
    );
    assert_eq!(closed_retry, closed);
    let old = d.call(
        "closed-reconcile",
        json!({"op":"reconcile","id":"sign-0","command":remote(ch,0)}),
    );
    assert_eq!(old["status"], "invalidated");
    let mut alias = remote(ch, 0);
    alias["channel"] = json!(ch.to_uppercase());
    assert_eq!(d.raw("uppercase-closed", alias, false, TOKEN)["ok"], false);
    // Malformed upstream setup txid serde is rejected before it can panic.
    let bad_setup = d.raw(
        "bad-setup",
        json!({"op":"setup","channel":ch,"setup":{"funding_outpoint":{"txid":"zz","vout":0}}}),
        false,
        TOKEN,
    );
    assert_eq!(bad_setup["ok"], false);
    d.call(
        "mark-loss",
        json!({"op":"mark_data_loss","channel":ch.to_uppercase()}),
    );
    d.kill();
    d.start(42);
    // Data-loss persistence also invalidates previously completed force-close signatures.
    assert_eq!(
        d.raw(
            "force-close",
            json!({"op":"force_close","channel":ch,"number":0}),
            false,
            TOKEN
        )["ok"],
        false
    );
    let lost=d.call("lost-reconcile",json!({"op":"reconcile","id":"force-close","command":{"op":"force_close","channel":ch,"number":0}}));
    assert_eq!(lost["status"], "invalidated");
    // Corrupt/malformed requests cannot terminate the owner.
    let mut malformed = UnixStream::connect(d.dir.join("node.sock")).unwrap();
    malformed.write_all(b"not-json\n").unwrap();
    let mut response = String::new();
    BufReader::new(malformed).read_line(&mut response).unwrap();
    assert_eq!(
        serde_json::from_str::<Value>(&response).unwrap()["ok"],
        false
    );
    d.call("identity", json!({"op":"identity"}));
    d.kill();
    let mut wrong = Command::new(env!("CARGO_BIN_EXE_nlightning-vls-gateway"))
        .arg(&d.dir)
        .arg(d.dir.join("token"))
        .arg(d.dir.join("admin"))
        .arg("regtest")
        .stdin(Stdio::piped())
        .spawn()
        .unwrap();
    wrong.stdin.take().unwrap().write_all(&[43u8; 32]).unwrap();
    assert!(!wrong.wait().unwrap().success());
}
