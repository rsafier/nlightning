#!/usr/bin/env bash
# Tests of scripts/run-cluster.sh's runner logic without a cluster (test harness phase 5): the matrix's admission queue
# (never more suites or namespaces than allowed), the flake rule (a failed class rerun alone once, rerun-green or a
# real failure), the hang timeout, skipped suites, the summary and the exit codes, and the single-suite mode. The test
# processes are scripts/tests/fake-xunit.sh (NLTG_RUN_CLUSTER_FAKE_TESTS); the plan and the summary come from the real
# nltg-cluster CLI, built here once. About 2 minutes; needs no cluster and no Docker.
#
# Usage: scripts/tests/run-cluster-tests.sh [configuration (default Release)]
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
config="${1:-Release}"
runner="$repo_root/scripts/run-cluster.sh"
fake="$repo_root/scripts/tests/fake-xunit.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/run-cluster-tests.XXXXXX")"
batches=()
passed=0
failed=0

cleanup() {
  local b
  for b in ${batches[@]+"${batches[@]}"}; do rm -rf "$repo_root/TestResults/cluster/$b"; done
  rm -rf "$work"
}
trap cleanup EXIT

echo "building the nltg-cluster CLI ($config)"
dotnet build "$repo_root/test/NLightning.Testing.Cluster.Cli" -c "$config" -f net10.0 -p:NltgTargetNet11=false \
  -p:MSBuildWarningsAsMessages=MSB4121 -nologo -v quiet > "$work/build.log" 2>&1 \
  || { tail -20 "$work/build.log"; echo "build failed"; exit 2; }

# run_case <name> <expected exit> <env assignments...> -- <runner args...>; sets $out, $results, $fake_dir
run_case() {
  local name="$1" expected="$2"
  shift 2
  local envs=()
  while [[ $# -gt 0 && "$1" != -- ]]; do envs+=("$1"); shift; done
  shift
  fake_dir="$work/$name"
  mkdir -p "$fake_dir"
  local batch="t-$name-$$"
  batch="${batch:0:24}"
  batches+=("$batch")
  results="$repo_root/TestResults/cluster/$batch"
  set +e
  out="$(env FAKE_DIR="$fake_dir" NLTG_RUN_CLUSTER_FAKE_TESTS="bash $fake" ${envs[@]+"${envs[@]}"} \
         "$runner" --no-build -c "$config" --id "$batch" "$@" 2>&1)"
  local code=$?
  set -e
  echo "$out" > "$fake_dir/runner.out"
  if [[ "$code" != "$expected" ]]; then
    fail "$name: exit $code, expected $expected"
    echo "$out" | tail -25
    return 1
  fi
  return 0
}

fail() { echo "FAIL $*"; failed=$((failed + 1)); }
ok() { echo "ok   $*"; passed=$((passed + 1)); }
expect() { # expect <name> <description> <command...>
  local name="$1" what="$2"
  shift 2
  if "$@"; then ok "$name: $what"; else fail "$name: $what"; fi
}
has() { grep -Eq -- "$1" <<< "$out"; }
no_violations() { [[ ! -s "$fake_dir/violations" ]]; }

set -e

# 1. The default matrix within 2 namespaces, 2 suites at once, a flaky class: rerun-green, exit 0. The LND suites
#    (their fixture is not wired in this tree, or they run) and tor are listed as skipped or run; postgres (3
#    namespaces) runs serially in 2; faults (2) in parallel.
if run_case matrix 0 FAKE_BUDGET=2 FAKE_JOBS=2 FAKE_SLEEP=2 "FAKE_WEIGHTS=postgres=2 faults=2 lnd=2" \
     FAKE_BEHAVIOR_cln=flaky:B -- --matrix -j 2 --max-namespaces 2; then
  expect matrix "admission never exceeded 2 suites or 2 namespaces" no_violations
  expect matrix "cln is rerun-green" grep -Eq "^cln +rerun-green" "$results/summary.txt"
  expect matrix "the flaky class was rerun alone, green" has "cln: Fake.cln.B rerun alone: green \(flake\)"
  expect matrix "tor is skipped as Docker only" has "tor: skipped: Docker only"
  expect matrix "postgres ran with -parallel none, waiting for each namespace's deletion" \
    grep -Eq -- "-postgres backend=cluster wait=1 .*-parallel none" "$fake_dir/args.log"
  expect matrix "faults ran with its collections in parallel" \
    bash -c "grep -- '-faults backend=cluster wait= ' '$fake_dir/args.log' | grep -vq -- '-parallel'"
  expect matrix "every suite runs on the cluster backend" bash -c "! grep -v 'backend=cluster ' '$fake_dir/args.log'"
  expect matrix "SQL Server tests are left out" bash -c "! grep -v -- '-trait- Database=SqlServer' '$fake_dir/args.log'"
  expect matrix "the summary counts the suites" has "rerun-green, 0 failed; [0-9]+ skipped"
  expect matrix "fixture ready is reported" grep -Eq "^eclair .* 1\.5s " "$results/summary.txt"
  expect matrix "a green suite's log is gzipped after the summary" \
    bash -c "test -f '$results/eclair/output.log.gz' && test ! -f '$results/eclair/output.log'"
  expect matrix "the summary reads a gzipped log again" bash -c \
    "dotnet '$repo_root/test/NLightning.Testing.Cluster.Cli/bin/$config/net10.0/nltg-cluster.dll' matrix summary \
       '$results' --repo '$repo_root' | grep -Eq '^eclair .* 1\.5s '"
fi

# 2. A class that fails again alone is a real failure: exit 1, the rerun is reported, the log is linked.
if run_case broken 1 FAKE_SLEEP=1 FAKE_BEHAVIOR_ldk=broken:A -- --matrix cln,ldk -j 2; then
  expect broken "ldk FAILED" grep -Eq "^ldk +FAILED" "$results/summary.txt"
  expect broken "cln green" grep -Eq "^cln +green" "$results/summary.txt"
  expect broken "the rerun failed again" has "ldk: Fake.ldk.A rerun alone: FAILED again"
  expect broken "the failed suite's log is linked and kept as it is" \
    bash -c "grep -Eq 'ldk: log TestResults/cluster/.*/ldk/output.log' '$results/summary.txt' && test -f '$results/ldk/output.log'"
  expect broken "the first error names the test" grep -Eq "^ldk .*First: First went wrong" "$results/summary.txt"
  expect broken "only the failed class was rerun" bash -c "grep -c -- '-ldk-r1 ' '$fake_dir/args.log' | grep -qx 1"
fi

# 3. A hang: the timeout stops the process, the suite is TIMEOUT, no rerun, exit 1.
if run_case hang 1 FAKE_SLEEP=1 FAKE_BEHAVIOR_postgres=hang -- --matrix postgres,cln --timeout 4s; then
  expect hang "postgres TIMEOUT" grep -Eq "^postgres +TIMEOUT .*hang timeout after" "$results/summary.txt"
  expect hang "no rerun after a hang" test ! -d "$results/postgres/rerun-1"
  expect hang "the hung process is gone" bash -c "! pgrep -f '[s]leep 600' > /dev/null"
fi

# 4. A crash (no results file) is a real failure without rerun; more failed classes than --rerun-max are not rerun.
if run_case crash 1 FAKE_SLEEP=1 FAKE_BEHAVIOR_cln=crash FAKE_BEHAVIOR_ldk=manyfail \
     -- --matrix cln,ldk --rerun-max 1; then
  expect crash "cln FAILED (no results)" grep -Eq "^cln +FAILED .*(no results file|exit 3)" "$results/summary.txt"
  expect crash "ldk FAILED without rerun" bash -c "grep -Eq '^ldk +FAILED' '$results/summary.txt' && ! test -d '$results/ldk/rerun-1'"
fi

# 4b. A fixture failure is not a flake: no rerun even though the class would pass alone; FAILED, exit 1.
if run_case fixture 1 FAKE_SLEEP=1 FAKE_BEHAVIOR_cln=fixture:A -- --matrix cln; then
  expect fixture "cln FAILED without rerun" \
    bash -c "grep -Eq '^cln +FAILED' '$results/summary.txt' && ! test -d '$results/cln/rerun-1'"
  expect fixture "the summary says it was a fixture" has "cln: 2 test\(s\) failed in a fixture \(no rerun\)"
fi

# 4c. A run that exits 0 without tests is FAILED and its log is kept (not gzipped): the summary links it.
if run_case empty 1 FAKE_SLEEP=1 FAKE_BEHAVIOR_ldk=empty -- --matrix ldk,cln; then
  expect empty "ldk FAILED: no tests ran" grep -Eq "^ldk +FAILED .*no tests ran" "$results/summary.txt"
  expect empty "its log stays where the summary points" \
    bash -c "test -f '$results/ldk/output.log' && test ! -f '$results/ldk/output.log.gz'"
  expect empty "the green suite's log is gzipped" test -f "$results/cln/output.log.gz"
fi

# 4d. Suites named explicitly but skipped (or nothing to run) are not green: exit 3.
if run_case named-skip 3 -- --matrix tor; then expect named-skip "tor listed as skipped" has "tor: skipped: Docker only"; fi
if run_case named-skip2 3 FAKE_SLEEP=1 -- --matrix cln,tor; then
  expect named-skip2 "cln still ran green" grep -Eq "^cln +green" "$results/summary.txt"
fi

# 4e. Namespaces that ended suites still hold (kept on failure) count against the budget: with 1 of 2 held, a
#     2-namespace suite is never started (NOT RUN, exit 1) and the queue never exceeds the budget.
if run_case held 1 FAKE_SLEEP=1 FAKE_HELD=1 "FAKE_WEIGHTS=faults=2" FAKE_BUDGET=2 \
     -- --matrix ldk,faults -j 1 --max-namespaces 2 --keep-on-failure; then
  expect held "faults not started" has "not starting faults: 1 namespace\(s\) of ended suites"
  expect held "faults reads NOT RUN" grep -Eq "^faults +NOT RUN" "$results/summary.txt"
  expect held "admission never exceeded the budget" no_violations
fi

# 5. The single-suite mode: N runs of a catalog suite on the cluster backend; tor and unknown suites are refused.
if run_case single 0 FAKE_SLEEP=1 -- -n 2 --suite cln; then
  expect single "2 runs green" has "2/2 run\(s\) green"
  expect single "the suite's trait and explicit mode" \
    grep -Eq -- "-1 backend=cluster wait= -explicit off -trait Category=Interop.Cln -trait- Database=SqlServer" "$fake_dir/args.log"
fi
if run_case single-class 0 FAKE_SLEEP=1 -- -n 1 --suite postgres --class Some.Class; then
  expect single-class "--class replaces the suite's classes, its trait stays" \
    grep -Eq -- "-explicit on -class Some.Class -trait Database=Postgres" "$fake_dir/args.log"
fi
if run_case tor 2 -- -n 1 --suite tor; then expect tor "refused as Docker only" has "Docker only"; fi
if run_case pending 0 FAKE_SLEEP=1 -- -n 1 --suite abcd; then
  expect pending "a suite whose cluster proof is pending runs when named" has "1/1 run\(s\) green"
fi
if run_case lndjobs 0 FAKE_SLEEP=1 -- -n 4 --suite lnd; then
  expect lndjobs "lnd's 2 namespaces per run cap the jobs at 3" has "capping --jobs at 3"
fi
if run_case unknown 2 -- --matrix cln,bogus; then expect unknown "names the unknown suite" has "unknown suite 'bogus'"; fi

# 6. Option checks.
if run_case both 2 -- --matrix --suite cln; then expect both "--matrix with --suite refused" has "exclude each other"; fi
if run_case runs 2 -- --matrix -n 2; then expect runs "--matrix with -n refused" has "runs each suite once"; fi
if run_case keep 2 -- --matrix --keep; then expect keep "--matrix with --keep refused" has "takes no --keep"; fi
if run_case budget 2 -- --matrix --max-namespaces 7; then expect budget "budget over 6 refused" has "1-6"; fi
if run_case tight 2 -- --matrix postgres --max-namespaces 1; then
  expect tight "a suite that never fits is refused" has "needs 2 namespace"
fi

# 7. Stopping the runner (TERM, as Ctrl-C's INT): its test processes get TERM, one that ignores it is KILLed 30 s later,
#    and the runner exits 130.
fake_dir="$work/stop"
mkdir -p "$fake_dir"
batch="t-stop-$$"
batches+=("$batch")
env FAKE_DIR="$fake_dir" NLTG_RUN_CLUSTER_FAKE_TESTS="bash $fake" FAKE_BEHAVIOR_cln=stubborn \
  "$runner" --no-build -c "$config" --id "$batch" --matrix cln > "$fake_dir/runner.out" 2>&1 &
runner_pid=$!
for _ in $(seq 1 40); do if [[ -s "$fake_dir/args.log" ]]; then break; fi; sleep 0.5; done
sleep 1
kill -TERM "$runner_pid"
set +e
wait "$runner_pid"
code=$?
set -e
out="$(cat "$fake_dir/runner.out")"
expect stop "the runner exits 130" test "$code" -eq 130
expect stop "the test process that ignored TERM was killed" has "ignored TERM for 30 s; killing it"
expect stop "no test process of the batch is left" bash -c "! pgrep -f '[f]ake-xunit.sh.*$batch' > /dev/null"

echo "$passed passed, $failed failed"
(( failed == 0 ))