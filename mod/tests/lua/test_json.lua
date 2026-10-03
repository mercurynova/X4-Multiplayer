local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function json()
	env.loadAll({ "x4mp_bridge.lua" })
	return X4MPBridge.json
end

-- UTF-8 written as decimal escapes so this file stays plain ASCII
local E_ACUTE = "\195\169"                 -- U+00E9
local CHECK = "\226\156\147"               -- U+2713
local EMOJI = "\240\159\152\128"           -- U+1F600

test("encode: scalars, arrays, nested objects with sorted keys", function()
	local j = json()
	eq(j.encode({ b = 1, a = { 1, 2, 3 }, c = { x = true, y = false } }), '{"a":[1,2,3],"b":1,"c":{"x":true,"y":false}}')
	eq(j.encode("hi"), '"hi"')
	eq(j.encode(true), "true")
	eq(j.encode(nil), "null")
	eq(j.encode(-12), "-12")
	eq(j.encode(1.5), "1.5")
	eq(j.encode(0.1), "0.1")
	eq(j.encode(0 / 0), "null")
	eq(j.encode(math.huge), "null")
end)

test("encode: empty table is an object, json.array makes []", function()
	local j = json()
	eq(j.encode({}), "{}")
	eq(j.encode(j.array({})), "[]")
	eq(j.encode({ list = j.array({}) }), '{"list":[]}')
end)

test("encode: rejects functions, mixed keys and runaway nesting", function()
	local j = json()
	local s, err = j.encode({ f = function() end })
	eq(s, nil)
	truthy(err)
	eq((j.encode({ [1] = "a", x = "b" })), nil)
	local deep = {}
	local cur = deep
	for _ = 1, 40 do cur.n = {} cur = cur.n end
	eq((j.encode(deep)), nil)
end)

test("round trip: ordinary document", function()
	local j = json()
	local doc = { v = 1, name = "Alice", ok = true, n = 42, f = 2.25, list = { 1, "two", { three = 3 } }, nested = { a = { b = { c = "d" } } } }
	local back = assert(j.decode(assert(j.encode(doc))))
	eq(j.encode(back), j.encode(doc))
	eq(back.list[3].three, 3)
	eq(back.nested.a.b.c, "d")
end)

test("round trip: escapes", function()
	local j = json()
	local s = 'quote " backslash \\ slash / newline \n tab \t cr \r bell \1 formfeed \f backspace \b nul-ish \31 del \127'
	local text = assert(j.encode({ s = s }))
	falsy(text:find("\n", 1, true), "raw newline must not appear in the output")
	t.contains(text, "\\u0001")
	t.contains(text, "\\n")
	eq(assert(j.decode(text)).s, s)
end)

test("round trip: unicode passes through as UTF-8", function()
	local j = json()
	local s = "h" .. E_ACUTE .. "llo " .. CHECK .. " " .. EMOJI
	local text = assert(j.encode({ s = s }))
	t.contains(text, E_ACUTE, "non-ASCII is not escaped")
	eq(assert(j.decode(text)).s, s)
end)

test("decode: \\u escapes and surrogate pairs become UTF-8", function()
	local j = json()
	eq(assert(j.decode('"\\u00e9"')), E_ACUTE)
	eq(assert(j.decode('"\\u2713"')), CHECK)
	eq(assert(j.decode('"\\ud83d\\ude00"')), EMOJI)
	eq(assert(j.decode('"\\u0041\\u0042"')), "AB")
	local v, err = j.decode('"\\ud83d"')
	eq(v, nil)
	t.contains(err, "surrogate")
	eq((j.decode('"\\ude00"')), nil)
end)

test("decode: numbers", function()
	local j = json()
	eq(j.decode("0"), 0)
	eq(j.decode("-3"), -3)
	eq(j.decode("1.5"), 1.5)
	eq(j.decode("1e3"), 1000)
	eq(j.decode("2.5E-1"), 0.25)
	eq(j.decode(" [1, 2 ,3 ] ")[2], 2)
end)

test("decode: null is dropped in objects and json.null in arrays", function()
	local j = json()
	local obj = assert(j.decode('{"a":null,"b":1}'))
	eq(obj.a, nil)
	eq(obj.b, 1)
	local arr = assert(j.decode("[1,null,3]"))
	eq(#arr, 3)
	eq(arr[2], j.null)
end)

test("decode: empty containers and whitespace", function()
	local j = json()
	eq(next(assert(j.decode(" { } "))), nil)
	eq(#assert(j.decode("[ ]")), 0)
	eq(assert(j.decode('{ "a" : [ true , false ] }')).a[2], false)
end)

test("decode: malformed input returns nil and a message", function()
	local j = json()
	for _, bad in ipairs({ "", "{", '{"a":', '{"a" 1}', "[1,]", "[1 2]", '{"a":1,}', "tru", "nul", '"abc', '"a\\qb"', "1 2",
		'{"a":1} x', '"\\u12"', "01x", '{1:2}', "\"line\nbreak\"" }) do
		local v, err = j.decode(bad)
		eq(v, nil, "decode(" .. bad .. ")")
		truthy(err, "message for " .. bad)
	end
	eq((j.decode(nil)), nil)
	eq((j.decode(5)), nil)
end)

test("decode: nesting depth is limited", function()
	local j = json()
	local text = string.rep("[", 100) .. string.rep("]", 100)
	local v, err = j.decode(text)
	eq(v, nil)
	t.contains(err, "deep")
end)
