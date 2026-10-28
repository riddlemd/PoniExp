#!/usr/bin/env sh
# Self-contained, single-file builds of poniexp-client (no .NET install needed to run them).
# Usage: scripts/publish.sh [rid ...]   (default: every supported runtime)
# Output: artifacts/<rid>/poniexp-client[.exe]
#
# macOS builds are signed with rcodesign when a signing certificate is present, so every build keeps
# the same identity and keeps its keychain access across updates (an ad-hoc signature changes per build).
#   PONIEXP_SIGN_P12        .p12 with the code-signing certificate   (default ~/.riddtech/codesign.p12)
#   PONIEXP_SIGN_P12_PASS   file holding its passphrase              (default <p12>.pass)
#   PONIEXP_SIGN=0          skip signing
set -eu
cd "$(dirname "$0")/.."
rids="${*:-win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64}"
p12="${PONIEXP_SIGN_P12:-$HOME/.riddtech/codesign.p12}"
p12pass="${PONIEXP_SIGN_P12_PASS:-$p12.pass}"

for rid in $rids; do
  dotnet publish src/PoniExp.Cli -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:DebugType=none -o "artifacts/$rid" -v q
  case "$rid" in
    osx-*)
      if [ "${PONIEXP_SIGN:-1}" = "0" ]; then
        echo "artifacts/$rid: not signed (PONIEXP_SIGN=0)" >&2
      elif [ -f "$p12" ] && command -v rcodesign >/dev/null 2>&1; then
        # No hardened runtime: the .NET host needs JIT, which hardened runtime blocks without entitlements.
        rcodesign sign --p12-file "$p12" --p12-password-file "$p12pass" \
          --binary-identifier poniexp-client "artifacts/$rid/poniexp-client" >/dev/null
      else
        echo "artifacts/$rid: NOT signed (no $p12 or no rcodesign); on macOS this build won't read keys saved by another build" >&2
      fi
      ;;
  esac
  echo "artifacts/$rid"
done
