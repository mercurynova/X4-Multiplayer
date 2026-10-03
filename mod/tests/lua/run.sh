#!/usr/bin/env sh
# Runs the Lua unit tests of the X4MP extension (mod/tests/lua). Needs LuaJIT or Lua 5.1 (CI: apt install lua5.1).
#   sh mod/tests/lua/run.sh            skips with a message (exit 0) when no interpreter is installed
#   sh mod/tests/lua/run.sh --require  exits 2 instead (CI)
# LUA=/path/to/interpreter overrides the search.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
require=0
[ "${1:-}" = "--require" ] && require=1

interp="${LUA:-}"
if [ -z "$interp" ]; then
	for c in luajit lua5.1 lua51 lua-5.1 lua; do
		if command -v "$c" >/dev/null 2>&1; then
			interp="$c"
			break
		fi
	done
fi

if [ -z "$interp" ]; then
	echo "Lua unit tests: no Lua interpreter found (luajit, lua5.1, lua). Install LuaJIT or Lua 5.1 to run them." >&2
	[ "$require" = 1 ] && exit 2
	echo "SKIPPED"
	exit 0
fi

echo "Lua unit tests: using $interp"
exec "$interp" "$here/run_all.lua"
