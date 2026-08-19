# ClrKernel.Jobs — the notebook job scheduler, its web UI, and the kernel that
# runs the notebooks, in one image.
#
# Build from the REPO ROOT (the build needs the whole solution):
#   docker build -f src/ClrKernel.Jobs/Dockerfile -t clrkernel-jobs .
#
# Run, mounting your notebooks and a volume for the run history:
#   docker run -p 5000:5000 \
#     -v "$PWD/samples:/notebooks:ro" \
#     -v clrkernel-jobs-data:/data \
#     clrkernel-jobs
#
# Then open http://localhost:5000. Add -e CLRKERNEL_JOBS_APIKEY=… whenever the
# port is reachable from anywhere but the host. Secrets for notification
# channels arrive the same way: -e CLRKERNEL_SECRET_MY_TOKEN=…

# --- the web UI ------------------------------------------------------------
FROM node:20-alpine AS webapp
WORKDIR /webapp
# package.json first: this layer stays cached until the dependencies change.
COPY src/ClrKernel.Jobs/webapp/package*.json ./
RUN npm ci
COPY src/ClrKernel.Jobs/webapp/ ./
# Vite writes ../wwwroot, i.e. /wwwroot in this stage.
RUN npm run build

# --- the tools -------------------------------------------------------------
# The 10.0 SDK matches CI; the projects target net8.0 with RollForward=Major, so
# they build here and run on the 10.0 runtime below.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The SPA must be in place before the build so it is picked up as content.
COPY --from=webapp /wwwroot/ src/ClrKernel.Jobs/wwwroot/
RUN dotnet publish src/ClrKernel.Jobs/ClrKernel.Jobs.csproj -c Release -o /app/jobs
# The kernel ships beside it rather than being installed from nuget, so the image
# always runs the kernel this commit was built against.
RUN dotnet publish src/ClrKernel/ClrKernel.csproj -c Release -o /app/kernel

# --- runtime ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# curl is here for the health check below; it is also what you reach for when
# debugging the API from inside the container.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/ ./

# Notebooks are mounted read-only; /data holds the run history and artifacts.
# The store must be explicit for serve — the image says sqlite; override the env
# (e.g. in compose) for postgres/sqlserver, together with CLRKERNEL_JOBS_CONNECTION.
ENV CLRKERNEL_JOBS_NOTEBOOKS=/notebooks \
    CLRKERNEL_JOBS_DATA=/data \
    CLRKERNEL_JOBS_CLRKERNEL=/app/kernel/ClrKernel \
    CLRKERNEL_JOBS_URLS=http://0.0.0.0:5000 \
    CLRKERNEL_JOBS_STORE=sqlite
EXPOSE 5000
VOLUME ["/data"]

# Runs as the non-root `app` user the .NET images already provide (uid 1654). A
# named volume inherits this ownership; a bind-mounted host directory does not, so
# `chown 1654 <dir>` on the host (or pass --user) if you bind-mount /data.
RUN mkdir -p /data /notebooks && chown -R app /data /notebooks
USER app

# The API answers /api/health without an API key precisely so this works.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s \
    CMD curl --fail --silent http://localhost:5000/api/health || exit 1

ENTRYPOINT ["/app/jobs/ClrKernel.Jobs"]
CMD ["serve"]
