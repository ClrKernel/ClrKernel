# Sample settings.json files for local testing

`settings.json` lives in the data dir and is the lowest config layer
(CLI flags and `CLRKERNEL_STUDIO_*` env vars override it). A `store` set here
satisfies serve's explicit-store requirement, so no flags are needed:

```bash
cp dev/settings/postgres.settings.json dev/data/settings.json
./dev/studio-dev.sh                          # or: clrkernel-studio serve --data-dir "$PWD/dev/data" ...
```

The postgres/sqlserver samples match `dev/docker-compose.dbs.yml` — bring the
databases up first:

```bash
docker compose -f dev/docker-compose.dbs.yml up -d postgres sqlserver
```

The passwords are local throwaways for containers bound to localhost (see the
compose file). Every key the file accepts, with its flag/env equivalents, is in
the configuration table in docs/studio.md — e.g. `notebooksRoot`, `maxParallelism`,
`relyingPartyId`, `origins`, `urls`, `gitEnabled`, `gitAuthorName`, `gitAuthorEmail`,
`gitPushRemote`.

**Stale database after pulling schema changes?** Run history is preview data —
if serve reports "table … already exists", delete the database (`rm dev/data/jobs.db`
for sqlite, or drop `clrkernel_studio` in the container) and start again.
