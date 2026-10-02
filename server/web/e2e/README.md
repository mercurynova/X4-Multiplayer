# Web GUI end-to-end tests (Playwright)

These drive the real GUI against the real `x4mp-server` exe, with FakeNode bots as the players. They are not part of
`npm test` (that is the fast vitest suite).

## Run

```powershell
dotnet build X4MP.sln                       # builds the server exe and the FakeNode exe (Debug)
cd server/web
npm ci
npx playwright install chromium            # once per machine
npm run e2e                                # all specs;  npx playwright test players   for one file
```

`npm run e2e` expects the Debug exes at `server/src/X4MP.Server/bin/Debug/net10.0/x4mp-server.exe` and
`tools/X4MP.FakeNode/bin/Debug/net10.0/X4MP.FakeNode.exe` (override with `X4MP_E2E_SERVER_EXE` /
`X4MP_E2E_FAKENODE_EXE`). It uses its own ports (web 5273, HTTP 47890, node TCP 47880, UDP 47881; `X4MP_E2E_WEB_PORT`,
`_HTTP_PORT`, `_TCP_PORT`, `_UDP_PORT`), so your own server can keep running. A failed run leaves a trace and screenshot
under `test-results/` (`npx playwright show-trace <trace.zip>`).

## How it works

- `playwright.config.ts` starts the Vite dev server (proxying to the e2e server via `X4MP_BACKEND`) and runs one worker,
  so specs share one server and one session and run in file order.
- `e2e/global-setup.ts` deletes and recreates the temp data dir (`<tmp>/x4mp-e2e-data`), starts the server exe on it with
  `X4MP__Net__MaxConnectionsPerIp=64`, reads `initial-admin-password.txt`, does the bootstrap admin's forced password
  change over the API, signs in again and saves the cookie to `e2e/.auth/admin.json` (git-ignored). Every spec therefore
  starts signed in as `admin`; a spec that tests anonymous behaviour uses
  `test.use({ storageState: { cookies: [], origins: [] } })` (see `smoke.spec.ts`). The server is killed at the end.
- `e2e/fixtures.ts` exports `test` and `expect` (import these, not `@playwright/test`):
  - `bots`: `bots.start({ command: 'client', name: 'Bob', duration: 60 })` spawns a FakeNode process and returns a `Bot`
    (`waitForLine(/in game/)`, `waitForExit()`, `output`, `running`, `stop()`). `command: 'swarm'` takes `clients`,
    `namePrefix`, `withAuthority`. Everything is killed when the test ends. A bot keeps its identity per `name`, so
    starting `Bob` again is the same player rejoining (that is how the ban/unban spec works).
  - `authority`: worker-scoped fake authority that uploads the first checkpoint and so starts the session. Request it in
    any spec that starts clients: `test.beforeEach(({ authority }) => {})`.
  - `api`: signed-in admin REST client (`api.get('/api/v1/players')`) for arranging or checking state.
- `e2e/env.ts`: ports and paths.

## Adding a spec

Create `e2e/<page>.spec.ts`, import from `./fixtures`, and use roles and labels for locators. Specs share one server, so
use unique bot names. Keep waits to real conditions (`expect(...).toBeVisible()`, `bot.waitForLine`), not sleeps.
