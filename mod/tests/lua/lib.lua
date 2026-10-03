-- Tiny test framework for the Lua unit runner (Lua 5.1 / LuaJIT compatible, no dependencies).
local T = { tests = {} }

function T.test(name, fn)
	T.tests[#T.tests + 1] = { name = name, fn = fn, file = T.currentFile }
end

local function describe(v)
	if type(v) == "string" then return string.format("%q", v) end
	return tostring(v)
end

function T.eq(actual, expected, msg)
	if actual ~= expected then
		error(string.format("%s: expected %s, got %s", msg or "eq", describe(expected), describe(actual)), 2)
	end
end

function T.truthy(v, msg)
	if not v then error((msg or "expected a truthy value") .. " (got " .. describe(v) .. ")", 2) end
end

function T.falsy(v, msg)
	if v then error((msg or "expected a falsy value") .. " (got " .. describe(v) .. ")", 2) end
end

--- true when `needle` (plain text) occurs anywhere in `haystack`
function T.contains(haystack, needle, msg)
	if type(haystack) ~= "string" or not haystack:find(needle, 1, true) then
		error((msg or "contains") .. ": " .. describe(needle) .. " not found in " .. describe(haystack), 2)
	end
end

--- Recursively searches keys and string values of `value` for `needle`; returns the path of the first hit or nil.
function T.findString(value, needle, path, seen)
	path, seen = path or "$", seen or {}
	if type(value) == "string" then
		if value:find(needle, 1, true) then return path end
	elseif type(value) == "table" and not seen[value] then
		seen[value] = true
		for k, v in pairs(value) do
			local hit = T.findString(k, needle, path .. ".<key>", seen) or T.findString(v, needle, path .. "." .. tostring(k), seen)
			if hit then return hit end
		end
	end
	return nil
end

function T.run(reset)
	local passed, failed = 0, 0
	for _, t in ipairs(T.tests) do
		reset()
		local ok, err = pcall(t.fn)
		if ok then
			passed = passed + 1
			print("  ok   " .. t.file .. ": " .. t.name)
		else
			failed = failed + 1
			print("  FAIL " .. t.file .. ": " .. t.name .. "\n         " .. tostring(err))
		end
	end
	print(string.format("%d passed, %d failed", passed, failed))
	return failed == 0
end

return T
