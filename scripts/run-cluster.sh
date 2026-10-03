#!/usr/bin/env bash
# Runs Kubernetes-harness test classes (Category=Cluster, Explicit; --project picks the test project) N times
# concurrently against a cluster (OrbStack's locally), each run in its own test process with its own
# NLTG_TEST_RUN_ID, so every run gets its own namespaces (nltg-spike-<run>[-<n>]). Plan
# docs/agents/TEST_HARNESS_PLAN.md R4, R13, R14; spike runner, not yet the full matrix runner of §5 step 5.
#
# Builds once, then starts the runs (at most --jobs in flight), writes each run's log and xunit XML under
# TestResults/cluster/<batch>/<run>/, reaps what a run left behind (its own namespaces only, after its process ended),
# and prints a summary table (also in TestResults/cluster/<batch>/summary.txt). Exit code 0 when every run is green.
# Failure diagnostics (pod logs, describe, events, PVC/PV, node state; ClusterDiagnostics) go to
# TestResults/cluster/<batch>/<run>/diag/<test or fixture>/<namespace>/, and the summary lists them per failed run.
#
# Usage: scripts/run-cluster.sh [options] [-- extra xunit v3 runner args]
#   -n, --runs N          runs (default 3)
#   -j, --jobs J          runs in flight at once (default N; never more than 6)
#   -c, --config C        build configuration (default Release)
#   -f, --framework F     target framework (default net10.0)
#   -p, --project P       the test project: cluster (default, test/NLightning.Testing.Cluster.Tests), integration
#                         (test/NLightning.Integration.Tests: our in-process node in cluster topologies, the
#                         NLightning.Integration.Tests.Cluster namespace) or a path to a test project directory
#       --class X         a test class to run (repeatable; default: every Category=Cluster test)
#       --method X        a test method (repeatable; xunit v3 wildcards allowed)
#       --context C       kubeconfig context (default $NLTG_KUBE_CONTEXT, else orbstack)
#       --id ID           batch id (default rc-<UTC timestamp>); run i gets <id>-<i>
#       --no-build        use the existing build
#       --reap-orphans    first reap runs whose owner process is gone or which are past their TTL
#       --keep            keep the namespaces (NLTG_KEEP_NAMESPACE=1; the reaper then leaves them until their TTL)
#       --keep-on-failure keep only the namespaces of failed runs (NLTG_KEEP_NAMESPACE=failure; reaped after their TTL)
#       --diag M          when to collect diagnostics: failure (default), always or off (NLTG_CLUSTER_DIAG)
#       --trait T         the xunit trait filter instead of Category=Cluster (e.g. Category=ClusterFailureProof)
#       --explicit M      xunit's -explicit mode: only (default: the Cluster tests are Explicit), on or off
#       --suite S         a ported suite on the cluster backend (NLTG_TEST_BACKEND=cluster): cln = the CLN interop
#                         suite (-p integration, --trait Category=Interop.Cln, --explicit off; its 4 Explicit capture
#                         tests stay out unless --explicit on is given); eclair = the Eclair interop suite
#                         (--trait Category=Interop.Eclair, --explicit off; its 1 Explicit test likewise); ldk =
#                         the LDK interop suite (--trait Category=Interop.Ldk, --explicit off); postgres =
#                         Docker/PostgresTests and the Explicit Cluster/Live/ServerDatabaseClusterTests on a Postgres
#                         pod (--trait Database=Postgres, --explicit on; MultiNodeHarnessTests' Postgres fact needs the
#                         LND fixture and stays out); faults = the partition and ZMQ-loss tests
#                         (Cluster/Live/PartitionClusterTests, ChainMonitorZmqClusterTests); lnd = the LND suite
#                         (the Docker, Docker.Utils and Docker.Mock namespaces, no trait filter, SQL Server left out
#                         with -notrait Database=SqlServer, --explicit off) on LightningRegtestNetworkFixture's cluster
#                         backend, its collections one at a time (-parallel none: at most 2 namespaces, the
#                         collection's network and MultiNodeHarnessTests' own Postgres); --class/--method replace
#                         a suite's classes (or namespaces); each run gets its own namespace(s)
#
# Example: the scaffold's namespace test 3 times at once
#   scripts/run-cluster.sh -n 3 --method '*ARunDeploysABusyboxStatefulSet*'
# Example: our in-process node against CLN and LND pods, 3 runs at once
#   scripts/run-cluster.sh -n 3 -p integration --class NLightning.Integration.Tests.Cluster.Live.InProcessNodeClusterTests
# Example: the whole CLN interop suite on the cluster, 3 runs at once (one CLN class: add --class)
#   scripts/run-cluster.sh -n 3 --suite cln
# Example: the Eclair interop suite on the cluster, one run
#   scripts/run-cluster.sh -n 1 --suite eclair
# Example: the LDK interop suite on the cluster, alone
#   scripts/run-cluster.sh -n 1 --suite ldk
# Example: the Postgres round trips on a Postgres pod, and the partition tests (test harness phase 4)
#   scripts/run-cluster.sh -n 1 --suite postgres; scripts/run-cluster.sh -n 1 --suite faults
# Example: the LND suite on the cluster, alone (test harness phase 3); one class of it
#   scripts/run-cluster.sh -n 1 --suite lnd
#   scripts/run-cluster.sh -n 1 --suite lnd --class NLightning.Integration.Tests.Docker.ReestablishFlowTests
#
# Never runs Docker suites and never touches namespaces outside nltg-spike-*: the test processes create only their
# own namespaces, and the reaper only deletes harness run namespaces (nltg-cluster reap, RunReaper).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runs=3
jobs=""
config=Release
framework=net10.0
project=cluster
context="${NLTG_KUBE_CONTEXT:-orbstack}"
batch=""
build=1
reap_orphans=0
keep=0
diag="${NLTG_CLUSTER_DIAG:-failure}"
trait="Category=Cluster"
explicit=only
suite=""
suite_classes=()
suite_namespaces=()
suite_args=()
backend=""
filters=()
extra=()

die() { echo "run-cluster: $*" >&2; exit 2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    -n|--runs) runs="${2:?}"; shift 2 ;;
    -j|--jobs) jobs="${2:?}"; shift 2 ;;
    -c|--config) config="${2:?}"; shift 2 ;;
    -f|--framework) framework="${2:?}"; shift 2 ;;
    -p|--project) project="${2:?}"; shift 2 ;;
    --class) filters+=(-class "${2:?}"); shift 2 ;;
    --method) filters+=(-method "${2:?}"); shift 2 ;;
    --context) context="${2:?}"; shift 2 ;;
    --id) batch="${2:?}"; shift 2 ;;
    --no-build) build=0; shift ;;
    --reap-orphans) reap_orphans=1; shift ;;
    --keep) keep=1; shift ;;
    --keep-on-failure) keep=failure; shift ;;
    --diag) diag="${2:?}"; shift 2 ;;
    --trait) trait="${2:?}"; shift 2 ;;
    --explicit) explicit="${2:?}"; explicit_set=1; shift 2 ;;
    --suite)
      suite="${2:?}"; shift 2
      case "$suite" in
        cln) project=integration; trait="Category=Interop.Cln"; backend=cluster
             [[ -n "${explicit_set:-}" ]] || explicit=off ;;
        eclair) project=integration; trait="Category=Interop.Eclair"; backend=cluster
             [[ -n "${explicit_set:-}" ]] || explicit=off ;;
        ldk) project=integration; trait="Category=Interop.Ldk"; backend=cluster
             [[ -n "${explicit_set:-}" ]] || explicit=off ;;
        postgres) project=integration; trait="Database=Postgres"; backend=cluster
                  suite_classes=(NLightning.Integration.Tests.Docker.PostgresTests
                                 NLightning.Integration.Tests.Cluster.Live.ServerDatabaseClusterTests)
                  [[ -n "${explicit_set:-}" ]] || explicit=on ;;
        faults) project=integration; trait="Category=Cluster"; backend=cluster
                suite_classes=(NLightning.Integration.Tests.Cluster.Live.PartitionClusterTests
                               NLightning.Integration.Tests.Cluster.Live.ChainMonitorZmqClusterTests) ;;
        lnd) project=integration; trait=""; backend=cluster
             suite_namespaces=(NLightning.Integration.Tests.Docker NLightning.Integration.Tests.Docker.Utils
                               NLightning.Integration.Tests.Docker.Mock)
             suite_args=(-notrait Database=SqlServer -parallel none)
             [[ -n "${explicit_set:-}" ]] || explicit=off ;;
        *) die "--suite $suite: unknown suite (cln, eclair, ldk, postgres, faults, lnd)" ;;
      esac ;;
    -h|--help) awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "$0"; exit 0 ;;
    --) shift; extra=("$@"); break ;;
    *) die "unknown argument $1 (see --help)" ;;
  esac
done

# A suite's own classes, unless --class/--method narrowed it
if (( ${#filters[@]} == 0 )) && [[ -n "${suite_classes[*]:-}" ]]; then
  for class in "${suite_classes[@]}"; do filters+=(-class "$class"); done
fi
if (( ${#filters[@]} == 0 )) && [[ -n "${suite_namespaces[*]:-}" ]]; then
  for ns in "${suite_namespaces[@]}"; do filters+=(-namespace "$ns"); done
fi
trait_args=()
[[ -n "$trait" ]] && trait_args=(-trait "$trait")

[[ "$runs" =~ ^[0-9]+$ && "$runs" -ge 1 ]] || die "--runs must be a positive number"
jobs="${jobs:-$runs}"
[[ "$jobs" =~ ^[0-9]+$ && "$jobs" -ge 1 ]] || die "--jobs must be a positive number"
[[ "$diag" =~ ^(failure|always|off)$ ]] || die "--diag must be failure, always or off"
[[ "$explicit" =~ ^(only|on|off)$ ]] || die "--explicit must be only, on or off"
(( jobs > 6 )) && { echo "run-cluster: capping --jobs at 6 (the spike's namespace cap)"; jobs=6; }
batch="${batch:-rc-$(date -u +%Y%m%d%H%M%S)}"
batch="$(echo "$batch" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9\n' '-' | sed 's/^-*//; s/-*$//')"
[[ -n "$batch" && ${#batch} -le 34 ]] || die "--id must leave 1-34 characters of [a-z0-9-]"

case "$project" in
  cluster) test_project="$repo_root/test/NLightning.Testing.Cluster.Tests" ;;
  integration) test_project="$repo_root/test/NLightning.Integration.Tests" ;;
  *) test_project="$(cd "$project" 2> /dev/null && pwd)" || die "--project $project: no such directory" ;;
esac
cli_project="$repo_root/test/NLightning.Testing.Cluster.Cli"
test_dll="$test_project/bin/$config/$framework/$(basename "$test_project").dll"
cli_dll="$cli_project/bin/$config/$framework/nltg-cluster.dll"
results="$repo_root/TestResults/cluster/$batch"
mkdir -p "$results"

export NLTG_KUBE_CONTEXT="$context"
cli() { dotnet "$cli_dll" "$@" --context "$context"; }

# 1. Build once (both the tests and the CLI), never per run.
if (( build )); then
  echo "run-cluster: building ($config, $framework)"
  for project in "$test_project" "$cli_project"; do
    dotnet build "$project" -c "$config" -f "$framework" -p:NltgTargetNet11=false \
      -p:MSBuildWarningsAsMessages=MSB4121 -nologo -v quiet > "$results/build-$(basename "$project").log" 2>&1 \
      || { tail -30 "$results/build-$(basename "$project").log"; die "build of $(basename "$project") failed"; }
  done
fi
[[ -f "$test_dll" && -f "$cli_dll" ]] || die "missing build output ($test_dll, $cli_dll); drop --no-build"

kubectl --context "$context" version --request-timeout=10s > /dev/null 2>&1 \
  || die "the cluster of context '$context' does not answer"

if (( reap_orphans )); then
  echo "run-cluster: reaping orphaned runs"
  cli reap | tee "$results/reap-before.txt"
fi

# 2. The runs: each its own process and run id; at most $jobs in flight.
batch_start=$(date +%s)
echo "run-cluster: batch $batch, $runs run(s), $jobs at once, context $context, results in $results${suite:+, suite $suite on the cluster backend}"
declare -a pids=() ids=()
running() { local n=0 pid; for pid in "${pids[@]}"; do kill -0 "$pid" 2> /dev/null && n=$((n + 1)); done; echo "$n"; }

start_run() {
  local id="$1" dir="$results/$1"
  mkdir -p "$dir"
  (
    start=$(date +%s)
    set +e
    NLTG_TEST_RUN_ID="$id" NLTG_KEEP_NAMESPACE="$keep" NLTG_CLUSTER_DIAG="$diag" NLTG_CLUSTER_DIAG_DIR="$dir/diag" \
      NLTG_TEST_BACKEND="${backend:-${NLTG_TEST_BACKEND:-}}" \
      dotnet "$test_dll" -explicit "$explicit" ${trait_args[@]+"${trait_args[@]}"} ${filters[@]+"${filters[@]}"} \
      ${suite_args[@]+"${suite_args[@]}"} -xml "$dir/results.xml" \
      -showLiveOutput -noColor "${extra[@]}" > "$dir/output.log" 2>&1
    code=$?
    echo "$code $(( $(date +%s) - start )) $start" > "$dir/exit"
  ) &
  pids+=("$!")
  ids+=("$id")
  echo "run-cluster: started $id (pid $!)"
}

for (( i = 1; i <= runs; i++ )); do
  while (( $(running) >= jobs )); do sleep 1; done
  start_run "$batch-$i"
done
for pid in "${pids[@]}"; do wait "$pid" || true; done

# 3. Cleanup: each run's own namespaces (their processes have ended, so the reaper sees their owner gone).
if [[ "$keep" != 1 ]]; then
  for id in "${ids[@]}"; do
    cli reap --run "$id" --wait > "$results/$id/reap-after.txt" 2>&1 || echo "run-cluster: reap of $id failed"
  done
fi

# 4. Summary.
python3 - "$results" "$batch_start" "$repo_root" "${ids[@]}" << 'PY' | tee "$results/summary.txt"
import os, re, sys, xml.etree.ElementTree as ET
results, batch_start, repo_root, ids = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4:]
rows = [("RUN", "EXIT", "TOTAL", "PASSED", "FAILED", "SKIPPED", "START", "END", "WALL", "SLOT", "NAMESPACES", "DIAG",
         "FIRST ERROR")]
failed = 0
diag_lines = []

def dumps_of(run_dir):
    """The dump folders of a run: diag/<test or fixture>/<namespace>[-n] (each has a summary.txt)."""
    root = os.path.join(run_dir, "diag")
    found = []
    for base, _, files in os.walk(root):
        if "summary.txt" in files:
            found.append(base)
    return sorted(found)
for run in ids:
    d = os.path.join(results, run)
    exit_file = os.path.join(d, "exit")
    code, wall, start = (open(exit_file).read().split() + ["?"] * 3)[:3] if os.path.exists(exit_file) else ("?",) * 3
    begin = f"+{int(start) - batch_start}s" if start.isdigit() else "?"
    end = f"+{int(start) + int(wall) - batch_start}s" if start.isdigit() and wall.isdigit() else "?"
    total = passed = fails = skipped = "?"
    error = ""
    xml = os.path.join(d, "results.xml")
    if os.path.exists(xml):
        a = ET.parse(xml).getroot().find("assembly")
        if a is not None:
            total, passed, fails, skipped = (a.get(k, "?") for k in ("total", "passed", "failed", "skipped"))
            for t in a.iter("test"):
                if t.get("result") == "Fail":
                    m = t.find("failure/message")
                    error = f"{t.get('method') or t.get('name')}: {(m.text or '').strip().splitlines()[0] if m is not None and m.text else 'failed'}"
                    break
    log = os.path.join(d, "output.log")
    namespaces, slot = [], "?"
    if os.path.exists(log):
        text = open(log, errors="replace").read()
        namespaces = sorted(set(re.findall(r"namespace (nltg-[a-z0-9-]+) created", text)))
        slot = "waited" if "waiting for a slot" in text or "over the cap" in text else "direct"
    dumps = dumps_of(d)
    kept = sorted(set(re.findall(r"namespace (nltg-[a-z0-9-]+) kept on failure", text))) if os.path.exists(log) else []
    run_failed = code != "0" or fails not in ("0", "?") or total in ("0", "?")
    if run_failed:
        failed += 1
        if not error:
            error = "no tests ran" if total == "0" else f"exit {code}"
        diag_lines.append(f"{run}: " + (f"{len(dumps)} dump(s)" if dumps else "no diagnostics collected")
                          + (f", kept namespaces {','.join(kept)}" if kept else ""))
        diag_lines.extend(f"  {os.path.relpath(p, repo_root)}" for p in dumps)
    rows.append((run, code, total, passed, fails, skipped, begin, end, f"{wall}s", slot, ",".join(namespaces) or "-",
                 str(len(dumps)), error[:120]))
widths = [max(len(r[i]) for r in rows) for i in range(len(rows[0]))]
for r in rows:
    print("  ".join(c.ljust(w) for c, w in zip(r, widths)).rstrip())
print(f"{len(ids) - failed}/{len(ids)} run(s) green")
if diag_lines:
    print("diagnostics of the failed runs:")
    for line in diag_lines:
        print(line)
sys.exit(1 if failed else 0)
PY
status=${PIPESTATUS[0]}

echo "run-cluster: run namespaces left under nltg-spike:"
cli list || true
exit "$status"