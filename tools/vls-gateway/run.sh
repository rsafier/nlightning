#!/usr/bin/env bash
set -euo pipefail
revision=cb8a64c71d3b214951e752281f05b9090e77f074
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cache_dir=${VLS_GATEWAY_CACHE:-${XDG_CACHE_HOME:-${HOME}/.cache}/nlightning/vls-gateway-${revision}}
source_dir=${cache_dir}/source
run_dir=${cache_dir}/run
command -v git >/dev/null
command -v cargo >/dev/null
cargo +1.94.0 --version >&2
mkdir -p -- "$cache_dir"
if [[ ! -d "$source_dir/.git" ]]; then
    git clone --no-checkout https://gitlab.com/lightning-signer/validating-lightning-signer.git "$source_dir"
    git -C "$source_dir" checkout --detach "$revision"
fi
if [[ $(git -C "$source_dir" rev-parse HEAD) != "$revision" ]]; then
    echo "VLS cache has the wrong revision; use another VLS_GATEWAY_CACHE." >&2
    exit 1
fi
git -C "$source_dir" diff --quiet
git -C "$source_dir" diff --cached --quiet
mkdir -p -- "$run_dir"
cp -- "$script_dir/Cargo.toml" "$script_dir/Cargo.lock" "$script_dir/main.rs" "$script_dir/process_test.rs" "$run_dir/"
case ${1:-} in
    test)
        shift
        cargo +1.94.0 test --locked --manifest-path "$run_dir/Cargo.toml" "$@"
        ;;
    build)
        shift
        cargo +1.94.0 build --locked --manifest-path "$run_dir/Cargo.toml" "$@"
        ;;
    *)
        cargo +1.94.0 build --locked --manifest-path "$run_dir/Cargo.toml" >&2
        exec "$run_dir/target/debug/nlightning-vls-gateway" "$@"
        ;;
esac
