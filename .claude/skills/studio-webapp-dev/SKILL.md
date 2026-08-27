---
name: studio-webapp-dev
description: Run and check the ClrKernel Studio web app (src/ClrKernel.Studio/webapp) in a browser. Use whenever verifying, debugging, or iterating on the Jobs/Studio SPA, its pages, Monaco editors, or the API behind it — and before writing any Playwright check against it. Covers the dev loop, an isolated instance, and the stale-bundle trap.
---

# Checking work in the Studio web app

## Run it the way a person does

`dev/studio-dev.sh` is the whole loop: `dotnet watch` on the API and the Vite dev
server, which proxies `/api` to it. **Open the Vite port, not the API port.**

Use a throwaway workspace — `dev/data` and `dev/notebooks` are the user's own
files. Non-default ports, so nothing collides with an instance they are running.

````bash
S=<scratchpad>/live
mkdir -p $S/nb $S/data
printf '# Extract\n\n```sql\nSELECT 1\n```\n' > $S/nb/etl.nb.md

# Once: the git workflow, which most of the app needs (Files, editing, promotion).
dotnet run --project src/ClrKernel.Studio -f net8.0 -- \
    git init --notebooks "$S/nb" --data-dir "$S/gitcfg"

DATA_DIR=$S/data API_PORT=5091 UI_PORT=5181 \
    nohup ./dev/studio-dev.sh $S/nb > $S/dev.log 2>&1 &
echo $! > $S/pid

until curl -sf localhost:5091/api/health >/dev/null; do sleep 2; done
````

Then drive **`http://localhost:5181`**. Teardown: `kill -- -$(cat $S/pid)` — the
process group, because `dotnet watch` spawns the app as a child and killing only
the parent leaves the port held.

Read `$S/dev.log` when something does not come up. It is where the API's refusals
land, and they are usually sentences rather than stack traces.

- **Edit a `.tsx`/`.css`** → live in the browser. No build. No restart.
- **Edit a `.cs`** → `dotnet watch` hot-patches or restarts; refresh.
- **Edit a notebook / `*.jobs.yaml`** → next request or scheduler tick.

## The trap this exists to avoid

Do **not** check UI work by running `npm run build` and then serving the packaged
app with `dotnet run --no-build`. `wwwroot` is copied into the output directory at
*build* time, so skipping the build serves the **previous bundle** — and that is
indistinguishable from a fix that did not work. It has already cost an hour: two
runs "reproducing" a bug that was fixed.

If you ever do use that path, prove the bundle is current before believing a
result:

```bash
diff <(curl -s localhost:PORT/ | grep -o 'assets/index[^"]*\.js') \
     <(grep -o 'assets/index[^"]*\.js' src/ClrKernel.Studio/wwwroot/index.html)
```

Vite serves `/src/main.tsx` — source, no bundle — so on the dev loop staleness is
not possible. That is the main reason to prefer it.

The packaged path is only right when the thing under test *is* the packaging:
`wwwroot` contents, the tool package, the Docker image.

## Playwright, in this app

```python
# Passkeys are the only way in. A virtual authenticator, before the first goto.
cdp = page.context.new_cdp_session(page)
cdp.send('WebAuthn.enable')
cdp.send('WebAuthn.addVirtualAuthenticator', {'options': {
    'protocol': 'ctap2', 'transport': 'internal', 'hasResidentKey': True,
    'hasUserVerification': True, 'isUserVerified': True,
    'automaticPresenceSimulation': True}})
page.goto(f'{B}/', wait_until='networkidle')
page.fill('input[placeholder="Ada Lovelace"]', 'Ada Lovelace')
page.get_by_role('button', name='Create the admin account').click()
page.wait_for_url(lambda u: not u.endswith('/setup'), timeout=15000)
```

- **Always** `page.on('pageerror', ...)` and assert it stayed empty. A React error
  boundary makes a thrown exception look like an empty panel.
- **Monaco renders spaces as `\xa0`.** `inner_text().replace('\xa0', ' ')` before
  any substring assertion, or a correct page fails.
- **Assert on the request body, not the rendered result**, when the question is
  "what did it ask the server to do". `page.on('request', ...)` and read
  `req.post_data`. Counting result tabs is a proxy; the SQL on the wire is not.
- **Route handlers must be async.** A sync `page.route` handler blocks the browser
  and will "prove" working code broken.
- **No driver delivers a full HTML5 drag.** `drag_to` fires everything on the
  source; hand-rolled mouse events give `dragstart`/`dragover` but never `drop`.
  Split it: a real pointer for the markup, a dispatched `drop` with a real
  `DataTransfer` for the arithmetic.
- Setup runs once per data dir. Re-run the reset before a script that creates the
  admin account, or `page.fill` times out on a sign-in screen.

## A check that cannot fail is not a check

Before believing a green run, **break the thing it covers and watch it go red.**
Revert the fix, delete the guard, point it at a dead port. Every check written
this session that mattered was verified that way, and two of them were wrong
until it was.

## Things that need a real database

The connections tree, `Select Top 1000`, scripting, and SQL completion all need
live metadata. `docker compose -f dev/docker-compose.dbs.yml up -d sqlserver`,
then create the connection through the API from inside the page:

```python
page.evaluate('''async () => (await fetch('/api/connections', {
  method: 'POST', headers: {'Content-Type': 'application/json'},
  body: JSON.stringify({ name: 'Alpha', scope: 'mine', type: 'SqlServer',
    settings: { server: 'localhost,51433', database: 'demo', user: 'sa',
                auth: 'sql', encrypt: 'false', trustServerCertificate: 'true' },
    secretRef: 'DEMO' }) })).json()''')
```

`auth: 'sql'` matters — the provider defaults to `integrated`, which on macOS
fails as `Login failed for user ''`. The password comes from
`CLRKERNEL_SECRET_DEMO` in the server's environment; passwords are never stored
in notebooks or config. Stop the container when finished.

## Before committing UI work

`npx tsc --noEmit -p tsconfig.json` and `npx vitest run` from
`src/ClrKernel.Studio/webapp`, plus `./build.sh Test` and
`dotnet format ClrKernel.slnx --verify-no-changes` if any C# moved. CI runs
format **first**, so unformatted code never reaches the build.
