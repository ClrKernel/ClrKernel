# ClrKernel.Studio — the notebook job scheduler, its web UI, and the kernel that
# runs the notebooks, in one image.
#
# Build from the REPO ROOT (the build needs the whole solution):
#   docker build -f src/ClrKernel.Studio/Dockerfile -t clrkernel-studio .
#
# Run, mounting your notebooks and a volume for the run history:
#   docker run -p 5000:5000 \
#     -v "$PWD/samples:/notebooks:ro" \
#     -v clrkernel-studio-data:/data \
#     clrkernel-studio
#
# There is no API key — the server is guarded by accounts and passkeys. The first
# account cannot be created through the published port: setup insists the caller
# is on loopback, and docker forwards through the bridge. Ask for an invite:
#   docker exec <container> clrkernel-studio new-admin-invite
# then open the /invite/<code> path it prints on the port you published. See
# docs/docker.md, including the reverse-proxy setup (passkeys need the real
# hostname in CLRKERNEL_STUDIO_RPID and HTTPS at the edge).
#
# Secrets for notification channels and database passwords arrive as
# environment variables: -e CLRKERNEL_SECRET_MY_TOKEN=…

# --- the web UI ------------------------------------------------------------
FROM node:20-alpine AS webapp
WORKDIR /webapp
# package.json first: this layer stays cached until the dependencies change.
COPY src/ClrKernel.Studio/webapp/package*.json ./
RUN npm ci
COPY src/ClrKernel.Studio/webapp/ ./
# Vite writes ../wwwroot, i.e. /wwwroot in this stage.
RUN npm run build

# --- the tools -------------------------------------------------------------
# The 10.0 SDK matches CI; the projects target net8.0 with RollForward=Major, so
# they build here and run on the 10.0 runtime below.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The SPA must be in place before the build so it is picked up as content.
COPY --from=webapp /wwwroot/ src/ClrKernel.Studio/wwwroot/
RUN dotnet publish src/ClrKernel.Studio/ClrKernel.Studio.csproj -c Release -o /app/studio
# The kernel ships beside it rather than being installed from nuget, so the image
# always runs the kernel this commit was built against.
RUN dotnet publish src/ClrKernel/ClrKernel.csproj -c Release -o /app/kernel

# --- runtime ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# curl serves the health check below; git powers the dev→prod workflow
# (`clrkernel-studio git init` on the mounted workspace). The other three give the
# server a credential store: gnome-keyring holds the saved passwords encrypted,
# dbus is how anything reaches it, and secret-tool (libsecret-tools) is what the
# secret chain in ClrKernel.Core.Secrets drives. About 35 MB, and it is what keeps
# passwords off the volume in the clear.
# unixodbc is the ODBC driver *manager* and ships no drivers: an ODBC connection
# fails with "data source name not found" until one is installed. odbc-postgresql
# is here so the type works out of the box for the database most people reach that
# way; anything else means adding its driver to this image. See docs/docker.md.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        curl git dbus gnome-keyring libsecret-tools unixodbc odbc-postgresql \
    && rm -rf /var/lib/apt/lists/*

# Two applications, two publish directories: /app/studio (scheduler + web app)
# and /app/kernel (the kernel it spawns per run). Their dependency graphs are
# resolved independently and some shared assemblies land on different versions,
# so publishing both into one directory would give one of them the other's copy.
COPY --from=build /app/ ./
COPY src/ClrKernel.Studio/docker-entrypoint.sh /usr/local/bin/docker-entrypoint
COPY src/ClrKernel.Studio/docker-run.sh /usr/local/bin/clrkernel-studio
RUN chmod +x /usr/local/bin/docker-entrypoint /usr/local/bin/clrkernel-studio

# /data holds the run history and artifacts. Notebooks can be mounted read-only
# for plain scheduling; the git workflow needs /notebooks writable (and owned by
# uid 1654) since edits and promotions commit there.
# The store must be explicit for serve — the image says sqlite; override the env
# (e.g. in compose) for postgres/sqlserver, together with CLRKERNEL_STUDIO_CONNECTION.
# Saved passwords go into a gnome-keyring on /data, encrypted with a password you
# supply at start — CLRKERNEL_STUDIO_KEYRING_PASSWORD_FILE (a docker/kubernetes
# secret) or CLRKERNEL_STUDIO_KEYRING_PASSWORD. Deliberately not defaulted: a key
# stored beside what it locks is decoration, so without one this server simply has
# nowhere to save a password and says so, and passwords arrive as
# CLRKERNEL_SECRET_* variables instead.
# /projects is where a *second* project goes. Without it there is nowhere: /data is
# refused on purpose (it holds the run history), and /notebooks is already the
# implicit project, so anything under it overlaps and is refused too. Naming it here
# means `-v studio-projects:/projects` works — a fresh named volume takes its
# ownership from the directory in the image.
ENV CLRKERNEL_STUDIO_NOTEBOOKS=/notebooks \
    CLRKERNEL_STUDIO_DATA=/data \
    CLRKERNEL_STUDIO_PROJECTS_ROOT=/projects \
    CLRKERNEL_STUDIO_CLRKERNEL=/app/kernel/ClrKernel \
    CLRKERNEL_STUDIO_URLS=http://0.0.0.0:5000 \
    CLRKERNEL_STUDIO_STORE=sqlite
EXPOSE 5000
VOLUME ["/data"]

# Runs as the non-root `app` user the .NET images already provide (uid 1654). A
# named volume inherits this ownership; a bind-mounted host directory does not, so
# `chown 1654 <dir>` on the host (or pass --user) if you bind-mount /data or /projects.
RUN mkdir -p /data /notebooks /projects && chown -R app /data /notebooks /projects
USER app

# The API answers /api/health without an API key precisely so this works.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s \
    CMD curl --fail --silent http://localhost:5000/api/health || exit 1

# The entrypoint starts the session bus and unlocks the keyring, then execs the
# server so it is PID 1 and `docker stop` reaches it. `clrkernel-studio` on PATH is
# the same thing for `docker exec`, which starts outside that session.
ENTRYPOINT ["/usr/local/bin/docker-entrypoint"]
CMD ["serve"]
