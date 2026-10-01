#!/usr/bin/env bash
# Downloads the pinned flatc (see flatc.lock.json) into tools/flatc/bin/ and verifies SHA-256.
# Exits non-zero on a checksum mismatch. Idempotent. Usage: tools/flatc/fetch-flatc.sh [--force]
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lock="$here/flatc.lock.json"
bin="$here/bin"

case "$(uname -s)" in
  Linux*) plat=linux ;;
  MINGW*|MSYS*|CYGWIN*) plat=windows ;;
  *) echo "unsupported OS $(uname -s)" >&2; exit 1 ;;
esac

# Minimal JSON reader without jq: python3 or python.
py="$(command -v python3 || command -v python || true)"
[ -n "$py" ] || { echo "python3 is required to read flatc.lock.json" >&2; exit 1; }
get() { "$py" -c "import json,sys;d=json.load(open(sys.argv[1]));p=d['platforms'][sys.argv[2]];print(d[sys.argv[3]] if sys.argv[3] in d else p[sys.argv[3]])" "$lock" "$plat" "$1"; }
version="$(get version)"; base="$(get baseUrl)"; asset="$(get asset)"; sha="$(get sha256)"; exe="$(get executable)"

stamp="$bin/flatc.version"
if [ "${1:-}" != "--force" ] && [ -x "$bin/$exe" ] && [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$version $sha" ]; then
  echo "flatc $version already present: $bin/$exe"; exit 0
fi

mkdir -p "$bin"
echo "Downloading $base/$asset"
curl -fsSL "$base/$asset" -o "$bin/$asset"
if command -v sha256sum >/dev/null; then actual="$(sha256sum "$bin/$asset" | cut -d' ' -f1)"; else actual="$(shasum -a 256 "$bin/$asset" | cut -d' ' -f1)"; fi
if [ "$actual" != "$sha" ]; then
  rm -f "$bin/$asset"
  echo "SHA-256 mismatch for $asset: expected $sha, got $actual" >&2
  exit 1
fi
(cd "$bin" && "$py" -c "import zipfile,sys;zipfile.ZipFile(sys.argv[1]).extractall('.')" "$asset")
rm -f "$bin/$asset"
chmod +x "$bin/$exe"
echo "$version $sha" > "$stamp"
"$bin/$exe" --version
