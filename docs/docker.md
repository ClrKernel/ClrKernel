# Running ClrKernel Studio in Docker

Every command here was run against the image built from this repository, on
Docker 29 / Compose v5. Nothing is abbreviated: each block is meant to be pasted
whole.

Ready-made files live in [`examples/docker/`](examples/docker/):

| file | what it is |
| --- | --- |
| `compose.yaml` | Studio + PostgreSQL on `http://localhost:8080` |
| `compose.nginx.yaml` | the same, with nginx in front and Studio's port unpublished |
| `nginx.conf` | the proxy config, with the TLS version commented beneath it |
| `notebooks/` | a notebook and a `*.jobs.yaml` so there is something to look at |

## Build the image

There is no published image yet, so build it. **From the repository root** — the
build needs the whole solution, and it compiles the web UI, the Studio tool and
the matching kernel into one image:

```bash
docker build --file src/ClrKernel.Studio/Dockerfile --tag clrkernel-studio .
```

The image carries its own kernel rather than installing one from NuGet, so it
always runs the kernel the image was built against. Nothing else to install.

## 1. Run one job and exit

The smallest useful thing. No database, no server, no ports — it runs the job and
prints the cells as they go:

```bash
docker run --rm \
  --volume "$PWD/docs/examples/docker/notebooks:/notebooks:ro" \
  clrkernel-studio run hello
```

```
Running hello (hello.nb.md)
[1/1] $"docker says {1 + 1}"
[1/1] ok (0.5s)
Succeeded: hello in 0.9s
```

`:ro` is deliberate: a one-shot run never writes to your notebooks. The artifact
paths it prints are inside the container and vanish with `--rm`; mount `/data` as
below if you want to keep them.

Other one-shot commands take the same shape — `list` prints the jobs it found and
`validate` exits non-zero if any `*.jobs.yaml` is broken, which makes it a
reasonable CI check:

```bash
docker run --rm \
  --volume "$PWD/docs/examples/docker/notebooks:/notebooks:ro" \
  clrkernel-studio validate
```

## 2. Run the scheduler and the web app

```bash
docker run --detach --name clrkernel-studio \
  --publish 8080:5000 \
  --volume "$PWD/docs/examples/docker/notebooks:/notebooks" \
  --volume clrkernel-studio-data:/data \
  clrkernel-studio
```

Then open **<http://localhost:8080>** and create the first account.

**Port 8080, not 5000, if you are on a Mac.** macOS runs its AirPlay receiver on
5000, and the container will refuse to start with `address already in use`. Any
free host port works; only the container side has to stay 5000.

`/data` holds the run history and the artifacts. It is a **named volume** here
because the image runs as the non-root `app` user (uid 1654) and a named volume
picks up that ownership by itself. A bind-mounted host directory does not — see
[Bind-mounting /data](#bind-mounting-data) below.

Useful while it runs:

```bash
docker logs --follow clrkernel-studio
docker inspect --format '{{.State.Health.Status}}' clrkernel-studio
curl --silent http://localhost:8080/api/health
```

`/api/health` answers without signing in, which is what the image's own
`HEALTHCHECK` uses. Everything else needs an account.

Stop and clean up:

```bash
docker stop clrkernel-studio
docker rm clrkernel-studio
docker volume rm clrkernel-studio-data     # deletes the run history
```

## 3. Compose, with PostgreSQL

sqlite is the image's default and is genuinely fine for one server. Use
PostgreSQL or SQL Server when you want the history somewhere you already back up.

```bash
docker compose --file docs/examples/docker/compose.yaml up --build --detach
```

```bash
curl --silent http://localhost:8080/api/health
docker compose --file docs/examples/docker/compose.yaml logs --follow studio
docker compose --file docs/examples/docker/compose.yaml down --volumes
```

Two things in that file are worth knowing rather than copying blindly.

**The store must be explicit.** `serve` refuses to start without one, so that a
server cannot silently land on sqlite and put the history somewhere you did not
mean. The image sets `CLRKERNEL_STUDIO_STORE=sqlite`; the compose file overrides
it with `postgres` and a `CLRKERNEL_STUDIO_CONNECTION`.

**Compose waits for the database.** Studio applies its migrations at startup and
would fail against a PostgreSQL still starting, so the `db` service has a
healthcheck and `studio` has `depends_on: condition: service_healthy`.

To check it really is using PostgreSQL and not quietly falling back:

```bash
docker compose --file docs/examples/docker/compose.yaml \
  exec db psql --username studio --dbname clrkernel_studio --command '\dt'
```

You should see `runs`, `promotions`, `notifications` and friends.

## 4. Behind nginx

```bash
docker compose --file docs/examples/docker/compose.nginx.yaml up --build --detach
```

Studio's own port is **not published** in that file — nginx on 8080 is the only
way in, which is the arrangement you want in front of a real network. Confirm it:

```bash
docker compose --file docs/examples/docker/compose.nginx.yaml ps
```

`proxy` shows `0.0.0.0:8080->80/tcp`; `studio` shows `5000/tcp` with no host
mapping at all.

### The two settings people get wrong

Sign-in is passkeys. A passkey is bound to a **domain**, and the server has no way
to know what hostname the browser typed — the proxy does. So you tell it:

```yaml
CLRKERNEL_STUDIO_RPID: localhost                 # the hostname in the address bar
CLRKERNEL_STUDIO_ORIGINS: http://localhost:8080  # the full URL, port and all
```

On a real deployment those become `studio.example.com` and
`https://studio.example.com`. Get them wrong and sign-in fails in a way that is
hard to read: the browser refuses to use the credential and the server never sees
an attempt.

WebAuthn also needs a **secure context** — HTTPS, or `localhost`. That is why the
example works over plain http on localhost, and why the commented TLS block in
`nginx.conf` is what you want on a real hostname. There is no WebSocket upgrade in
the config because the app does not use one; the UI polls.

Startup logs a warning whenever the server listens beyond localhost with
`rp-id` still `localhost`. In these examples that is expected — you really are
reaching it as `localhost`. On a real hostname it is telling you to fix `rp-id`
**before** anyone registers a passkey, because a passkey cannot be moved to
another domain afterwards.

### First-run setup does not work through a proxy — on purpose

Open `http://localhost:8080` behind nginx and the setup screen refuses:

> First-run setup has to be done from the machine running the server.

That is deliberate, not a misconfiguration. A fresh data directory makes whoever
reaches it first the Server Admin, so the check looks at the **caller's own IP**
and insists on loopback. Behind a proxy the caller is the proxy, and honouring
`X-Forwarded-For` here would mean trusting a header anyone can send.

Ask the container for an invite instead:

```bash
docker compose --file docs/examples/docker/compose.nginx.yaml \
  exec studio /app/jobs/ClrKernel.Studio new-admin-invite
```

```
Nb77U7_yzJeRmEvDBy7vuaPjupM
http://localhost:8080/invite/Nb77U7_yzJeRmEvDBy7vuaPjupM
Single use, expires 2026-09-04 13:35:58Z. Opening it creates a new Server Admin.
```

It builds that URL from `CLRKERNEL_STUDIO_ORIGINS`, so it is already the address
your browser should use. Open it, register a passkey, and you are the admin. The
same command is the way back in if every admin loses their device.

The full path is needed because the image's `ENTRYPOINT` **is** the application —
`docker compose exec studio new-admin-invite` would look for a program called
`new-admin-invite`. For the same reason, a shell needs `--entrypoint`:

```bash
docker run --rm --interactive --tty --entrypoint sh clrkernel-studio
```

## 5. The dev → prod git workflow

Editing notebooks in the web app, pushing to `test` and promoting to `prod` needs
the git workspace, and that means `/notebooks` is **writable** — Studio commits
there.

`git init` **rearranges the directory you point it at**: your notebooks move into
`test/` and `prod/` worktrees beside a `.repo.git`. That is the point, but do not
try it on the example folder in this repository — copy it somewhere first:

```bash
cp -R docs/examples/docker/notebooks ~/studio-notebooks
```

Then initialise it once:

```bash
docker run --rm \
  --volume "$HOME/studio-notebooks:/notebooks" \
  --volume clrkernel-studio-data:/data \
  clrkernel-studio git init
```

```
/notebooks: initialized; adopted 1 existing item(s) into test and promoted them
gitEnabled=true written to settings.json.
```

Your notebooks root now holds `test/` and `prod/` worktrees and a `.repo.git`.
The flag is written into `/data/settings.json`, so pass the **same** `/data`
volume when you then `serve` — otherwise the server will not know the workflow is
on. The way to tell it took is the environment list:

```bash
curl --silent http://localhost:8080/api/health
```

`"environments":["test","prod"]` means the workflow is on; `["default"]` means it
is not. (`"gitEnabled"` in the same answer is per-viewer and reads `false` until
you sign in, so it is not the thing to check.)

Use the same `--volume` for `/notebooks` as your server, and drop the `:ro` you
would use for plain scheduling.

<a id="bind-mounting-data"></a>

## Bind-mounting /data

A named volume inherits the image's uid 1654. A host directory does not, so the
container cannot write to it:

```bash
# Linux: give the directory to the uid the container runs as.
mkdir -p ./studio-data
sudo chown -R 1654 ./studio-data

docker run --detach --name clrkernel-studio \
  --publish 8080:5000 \
  --volume "$PWD/docs/examples/docker/notebooks:/notebooks" \
  --volume "$PWD/studio-data:/data" \
  clrkernel-studio
```

On Docker Desktop (macOS and Windows) the file sharing layer maps ownership for
you and the `chown` is usually unnecessary. On Linux it is not optional.

The alternative is to run as yourself, which avoids the ownership question
entirely at the cost of not running as the user the image expects:

```bash
docker run --detach --name clrkernel-studio \
  --publish 8080:5000 \
  --user "$(id -u):$(id -g)" \
  --volume "$PWD/docs/examples/docker/notebooks:/notebooks" \
  --volume "$PWD/studio-data:/data" \
  clrkernel-studio
```

## Configuration

Everything is an environment variable; the image sets the first five itself.

| variable | what it is |
| --- | --- |
| `CLRKERNEL_STUDIO_NOTEBOOKS` | notebooks root — `/notebooks` in the image |
| `CLRKERNEL_STUDIO_DATA` | run history and artifacts — `/data` |
| `CLRKERNEL_STUDIO_CLRKERNEL` | the kernel binary — `/app/kernel/ClrKernel` |
| `CLRKERNEL_STUDIO_URLS` | listen address — `http://0.0.0.0:5000` |
| `CLRKERNEL_STUDIO_STORE` | `sqlite` \| `postgres` \| `sqlserver` \| `files` |
| `CLRKERNEL_STUDIO_CONNECTION` | connection string for postgres/sqlserver |
| `CLRKERNEL_STUDIO_RPID` | the domain passkeys are bound to |
| `CLRKERNEL_STUDIO_ORIGINS` | origins the browser may present, `;`-separated |
| `CLRKERNEL_STUDIO_GIT` | `true` to enable the test/prod workflow |
| `CLRKERNEL_STUDIO_MAX_PARALLELISM` | concurrent runs (default 4) |
| `CLRKERNEL_STUDIO_RUN_RETENTION_DAYS` | delete runs older than this; `0` keeps everything |
| `CLRKERNEL_STUDIO_WORKTREE_IDLE_DAYS` | prune untouched personal worktrees (default 30) |
| `CLRKERNEL_SECRET_*` | secret *references* — channel tokens, database passwords |

**There is no API key.** The server is guarded by accounts and passkeys, and
nothing else. An earlier version of the Dockerfile's comments suggested setting
`CLRKERNEL_STUDIO_APIKEY`; no such setting has ever existed, and setting it does
nothing. If the port is reachable, what protects it is that an admin already
claimed the server — which is why doing setup before exposing it matters.

Passwords are never written into notebooks or config. A channel or a connection
holds a *reference*, and the value arrives as `CLRKERNEL_SECRET_<REF>`:

```bash
docker run --detach --name clrkernel-studio \
  --publish 8080:5000 \
  --volume "$PWD/docs/examples/docker/notebooks:/notebooks" \
  --volume clrkernel-studio-data:/data \
  --env CLRKERNEL_SECRET_SLACK_HOOK=xoxb-your-token-here \
  clrkernel-studio
```

## When it does not come up

```bash
docker logs clrkernel-studio
```

The refusals are sentences, and usually the first line:

- **`serve needs an explicit run-history store`** — set `CLRKERNEL_STUDIO_STORE`.
  It is deliberate: silently defaulting would put the history where you did not
  look for it.
- **`serve needs a database: user accounts and sessions have nowhere to live
  under --store files`** — `files` is for one-shot commands, not the server.
- **`address already in use`** — something else has the host port. On macOS,
  port 5000 is AirPlay.
- **Health check never goes healthy** — `docker inspect --format
  '{{json .State.Health}}' clrkernel-studio` shows the last probe's output.
