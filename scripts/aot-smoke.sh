#!/usr/bin/env bash
# NativeAOT publish and smoke test of the daemon (nltg) and the client (NL-338; CLAUDE.md "NativeAOT").
#
#   scripts/aot-smoke.sh [--framework net10.0|net11.0] [--rid <rid>] [--emulate] [--ipc] [--build-only|--skip-build]
#                        [--out <dir>]
#
# Publishes NLightning.Daemon and NLightning.Client with -c Release.Native -p:PublishAot=true (CRYPTO_NATIVE) for the
# host RID (osx-arm64, osx-x64, linux-x64, linux-arm64; NativeAOT does not cross-compile between operating systems),
# then runs the binaries in a throwaway HOME, so ~/.nltg is never read or written:
#   1. nltg --help                                  exit 0
#   2. nltg --network regtest --check-config        exit 0, "Configuration OK" (the template, bound by the generated
#                                                   binder and validated like a node start)
#   3. the same with a broken Bitcoin section       exit 1, the error named
#   4. nltg --network regtest --status              exit 0, "not running"
#   5. nltg --network regtest (start the node)      exit 1 with the NL-708 message: the AOT build cannot run EF Core yet
#   6. client --help                                exit 0
#   7. client --network regtest info (no node)      exit 1 with a clean "Error:" (no crash)
#
# --emulate builds instead of publishing (dotnet build -p:PublishAot=true): the JIT runs the build output with the
# NativeAOT feature switches (no dynamic code, no reflection-based System.Text.Json), which catches most AOT runtime
# failures without the ILCompiler; use it where publishing is not possible. --skip-build reuses the binaries in --out.
# DOTNET picks the dotnet executable (e.g. DOTNET=/usr/local/share/dotnet/dotnet for SDK 11).
#
# --ipc (needs Docker; it starts and removes the container nltg-aot-smoke-bitcoind, so wrap it in the machine's Docker
# lock where one exists, after a --build-only run outside it, then pass --skip-build): the AOT client against a running
# node. The node is the JIT daemon (Release, built into --out/jit-daemon; the AOT daemon cannot run the node yet,
# NL-708) on a regtest bitcoind (polarlightning/bitcoind:29.0) published on 127.0.0.1 ports NLTG_SMOKE_PORT_BASE+0..3
# (default 39440): the client runs info, listpeers, listchannels, getaddress, walletbalance, chainstatus,
# createinvoice, listinvoices, listpayments, listforwards, pendingsweeps, describegraph and listnodes (exit 0 each),
# then shutdown stops the node.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_exe="${DOTNET:-dotnet}"
framework="net10.0"
rid=""
emulate=false
skip_build=false
build_only=false
ipc=false
out=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --framework) framework="$2"; shift 2 ;;
    --rid) rid="$2"; shift 2 ;;
    --emulate) emulate=true; shift ;;
    --skip-build) skip_build=true; shift ;;
    --build-only) build_only=true; shift ;;
    --ipc) ipc=true; shift ;;
    --out) out="$2"; shift 2 ;;
    -h|--help) sed -n '2,33p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$rid" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) rid="osx-arm64" ;;
    Darwin-x86_64) rid="osx-x64" ;;
    Linux-x86_64) rid="linux-x64" ;;
    Linux-aarch64|Linux-arm64) rid="linux-arm64" ;;
    *) echo "Unknown host; pass --rid" >&2; exit 2 ;;
  esac
fi

mode="publish"
$emulate && mode="emulated"
[[ -z "$out" ]] && out="$repo_root/artifacts/aot/$framework-$rid-$mode"
mkdir -p "$out"

build_one() {
  local project="$1" name="$2"
  local log="$out/$name.log"
  local args=(-c Release.Native -f "$framework" -r "$rid" -p:PublishAot=true -p:MSBuildWarningsAsMessages=MSB4121
              -o "$out/$name")
  # SDK 11 multi-targets the src projects (src/Directory.Build.props); -f picks one
  echo "== $mode $project ($framework, $rid)"
  if $emulate; then
    "$dotnet_exe" build "$repo_root/src/$project" "${args[@]}" > "$log" 2>&1 || { tail -40 "$log"; exit 1; }
  else
    "$dotnet_exe" publish "$repo_root/src/$project" "${args[@]}" > "$log" 2>&1 || { tail -40 "$log"; exit 1; }
  fi
  local warnings
  warnings="$(grep -oE 'warning (IL|SYSLIB|CS)[0-9]+' "$log" | sort | uniq -c || true)"
  if [[ -n "$warnings" ]]; then
    echo "   warnings (see $log):"
    echo "$warnings" | sed 's/^/     /'
  else
    echo "   no IL/SYSLIB/CS warnings"
  fi
}

if ! $skip_build; then
  build_one NLightning.Daemon daemon
  build_one NLightning.Client client
  if $ipc || $build_only; then
    echo "== build JIT NLightning.Daemon ($framework) for the IPC checks"
    "$dotnet_exe" build "$repo_root/src/NLightning.Daemon" -c Release -f "$framework" \
      -p:MSBuildWarningsAsMessages=MSB4121 -o "$out/jit-daemon" > "$out/jit-daemon.log" 2>&1 \
      || { tail -40 "$out/jit-daemon.log"; exit 1; }
  fi
fi

$build_only && { echo "Built into $out"; exit 0; }

daemon="$out/daemon/NLightning.Daemon"
client="$out/client/NLightning.Client"
for exe in "$daemon" "$client"; do
  [[ -x "$exe" ]] || { echo "Missing $exe" >&2; exit 1; }
done
if ! $emulate; then
  echo "   daemon $(du -h "$daemon" | cut -f1), client $(du -h "$client" | cut -f1)"
fi

home="$(mktemp -d "${TMPDIR:-/tmp}/nltg-aot-smoke.XXXXXX")"
container="nltg-aot-smoke-bitcoind"
node_pid=""
cleanup() {
  [[ -n "$node_pid" ]] && kill "$node_pid" 2> /dev/null || true
  $ipc && docker rm -f "$container" > /dev/null 2>&1 || true
  rm -r "$home"
}
trap cleanup EXIT
failures=0

# Runs a binary in the throwaway HOME (no NLTG_ variables) and checks its exit code and output
check() {
  local name="$1" expected_code="$2" expected_text="$3"
  shift 3
  local output code=0
  local environment=(HOME="$home" PATH="$PATH" TMPDIR="${TMPDIR:-/tmp}")
  # An emulated (JIT) binary's apphost needs the runtime it was built against
  [[ -n "${DOTNET_ROOT:-}" ]] && environment+=(DOTNET_ROOT="$DOTNET_ROOT")
  output="$(env -i "${environment[@]}" "$@" 2>&1 < /dev/null)" || code=$?
  if [[ "$code" == "$expected_code" ]] && grep -qF -- "$expected_text" <<< "$output"; then
    echo "PASS $name"
  else
    echo "FAIL $name (exit $code, expected $expected_code and \"$expected_text\")"
    sed 's/^/     /' <<< "$output" | tail -20
    failures=$((failures + 1))
  fi
}

check "daemon --help" 0 "NLTG - NLightning Daemon" "$daemon" --help
check "daemon --check-config (template)" 0 "Configuration OK" "$daemon" --network regtest --check-config

config="$home/.nltg/regtest/appsettings.json"
cp "$config" "$config.orig"
sed -i.bak 's#"RpcEndpoint": "http://localhost:[0-9]*"#"RpcEndpoint": "localhost"#' "$config"
check "daemon --check-config (broken Bitcoin section)" 1 "Bitcoin:RpcEndpoint" \
  "$daemon" --network regtest --check-config
cp "$config.orig" "$config"

check "daemon --status" 0 "not running" "$daemon" --network regtest --status
check "daemon start refused (NL-708)" 1 "NL-708" "$daemon" --network regtest --password smoke-test
check "client --help" 0 "NLightning Node Client" "$client" --help
check "client info without a node" 1 "Error:" "$client" --network regtest info

if $ipc; then
  jit_daemon="$out/jit-daemon/NLightning.Daemon"
  [[ -x "$jit_daemon" ]] || { echo "Missing $jit_daemon (run without --skip-build first)" >&2; exit 1; }
  base="${NLTG_SMOKE_PORT_BASE:-39440}"
  rpc_port=$base zmq_block_port=$((base + 1)) zmq_tx_port=$((base + 2)) p2p_port=$((base + 3))

  echo "== start $container (regtest bitcoind)"
  docker rm -f "$container" > /dev/null 2>&1 || true
  docker run -d --name "$container" -p "127.0.0.1:$rpc_port:18443" -p "127.0.0.1:$zmq_block_port:28334" \
    -p "127.0.0.1:$zmq_tx_port:28335" polarlightning/bitcoind:29.0 bitcoind -regtest=1 -server=1 -rpcuser=smoke \
    -rpcpassword=smoke -rpcbind=0.0.0.0 -rpcallowip=0.0.0.0/0 -zmqpubrawblock=tcp://0.0.0.0:28334 \
    -zmqpubrawtx=tcp://0.0.0.0:28335 -fallbackfee=0.0002 > /dev/null
  rpc() {
    curl -s --user smoke:smoke -H 'content-type: text/plain;' \
      --data-binary "{\"jsonrpc\":\"1.0\",\"id\":\"smoke\",\"method\":\"$1\",\"params\":$2}" \
      "http://127.0.0.1:$rpc_port/"
  }
  for _ in $(seq 1 60); do
    rpc getblockcount '[]' | grep -q '"result"' && break
    sleep 1
  done
  rpc generatetodescriptor '[101,"raw(51)"]' > /dev/null

  echo "== start the JIT node (regtest, listening on 127.0.0.1:$p2p_port)"
  printf 'smoke-test-password' > "$home/password"
  chmod 600 "$home/password"
  node_environment=(HOME="$home" PATH="$PATH" TMPDIR="${TMPDIR:-/tmp}")
  [[ -n "${DOTNET_ROOT:-}" ]] && node_environment+=(DOTNET_ROOT="$DOTNET_ROOT")
  env -i "${node_environment[@]}" "$jit_daemon" --network regtest --password-file "$home/password" \
    "--Bitcoin:RpcEndpoint=http://127.0.0.1:$rpc_port" --Bitcoin:RpcUser=smoke --Bitcoin:RpcPassword=smoke \
    --Bitcoin:ZmqHost=127.0.0.1 "--Bitcoin:ZmqBlockPort=$zmq_block_port" "--Bitcoin:ZmqTxPort=$zmq_tx_port" \
    "--Node:ListenAddresses:0=127.0.0.1:$p2p_port" --Database:RunMigrations=true \
    > "$out/jit-node.log" 2>&1 < /dev/null &
  node_pid=$!

  ready=false
  for _ in $(seq 1 90); do
    if env -i "${node_environment[@]}" "$client" --network regtest info > /dev/null 2>&1; then
      ready=true
      break
    fi
    kill -0 "$node_pid" 2> /dev/null || break
    sleep 1
  done
  if ! $ready; then
    echo "FAIL the JIT node did not answer the AOT client (see $out/jit-node.log)"
    tail -30 "$out/jit-node.log" | sed 's/^/     /'
    failures=$((failures + 1))
  else
    check "client info" 0 "" "$client" --network regtest info
    check "client listpeers" 0 "" "$client" --network regtest listpeers
    check "client listchannels" 0 "" "$client" --network regtest listchannels
    check "client getaddress" 0 "bcrt1" "$client" --network regtest getaddress
    check "client walletbalance" 0 "" "$client" --network regtest walletbalance
    check "client chainstatus" 0 "" "$client" --network regtest chainstatus
    check "client createinvoice" 0 "lnbcrt" "$client" --network regtest createinvoice 1000 aot-smoke
    check "client listinvoices" 0 "" "$client" --network regtest listinvoices
    check "client listpayments" 0 "" "$client" --network regtest listpayments
    check "client listforwards" 0 "" "$client" --network regtest listforwards
    check "client pendingsweeps" 0 "" "$client" --network regtest pendingsweeps
    check "client describegraph" 0 "" "$client" --network regtest describegraph
    check "client listnodes" 0 "" "$client" --network regtest listnodes
    check "client shutdown" 0 "" "$client" --network regtest shutdown
    for _ in $(seq 1 30); do
      kill -0 "$node_pid" 2> /dev/null || { node_pid=""; break; }
      sleep 1
    done
    if [[ -n "$node_pid" ]]; then
      echo "FAIL the node did not stop after shutdown"
      failures=$((failures + 1))
    else
      echo "PASS node stopped"
    fi
  fi
fi

if [[ $failures -gt 0 ]]; then
  echo "$failures smoke check(s) failed ($mode, $framework, $rid)"
  exit 1
fi
echo "All smoke checks passed ($mode, $framework, $rid)"