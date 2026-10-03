"""Runs the Lua unit tests through Python lupa (Lua 5.1) when no stand-alone Lua is installed.

    pip install lupa
    python mod/tests/lua/run_lupa.py
"""
import os
import sys

try:
    from lupa.lua51 import LuaRuntime
except ImportError:  # older lupa builds
    from lupa import LuaRuntime  # type: ignore

here = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
lua = LuaRuntime(unpack_returned_tuples=True)
# run_all.lua ends with os.exit(code): turn it into a catchable error so the Python process can return the code
lua.execute('function os.exit(code) error("__EXIT__" .. tostring(code == true and 0 or (code == false and 1 or code or 0)), 0) end')
lua.globals().arg = lua.table_from({0: here + "/run_all.lua"})
try:
    lua.execute('dofile("%s/run_all.lua")' % here)
except Exception as ex:  # lupa wraps Lua errors
    msg = str(ex)
    if "__EXIT__" in msg:
        sys.exit(int(msg.split("__EXIT__")[1].split()[0].strip("'\"")))
    print(msg)
    sys.exit(1)
