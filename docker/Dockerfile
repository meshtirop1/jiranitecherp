# syntax=docker/dockerfile:1.7

# ---------------------------------------------------------------------------
# Build
#
# The SDK image only exists in this stage. What ships is the runtime image at
# the bottom, which has no compiler, no source and no package cache in it —
# smaller to pull, and a far smaller thing for an attacker to find useful.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /source

# Project files first, on their own, so `restore` is cached against them. Edit a
# .cs file and the restore layer is reused; edit a .csproj and only then does it
# run again. This is the difference between a ten second rebuild and a two
# minute one.
COPY JiranisokoTech.slnx ./
COPY src/JiranisokoTech.Domain/*.csproj src/JiranisokoTech.Domain/
COPY src/JiranisokoTech.Application/*.csproj src/JiranisokoTech.Application/
COPY src/JiranisokoTech.Infrastructure/*.csproj src/JiranisokoTech.Infrastructure/
COPY src/JiranisokoTech.Web/*.csproj src/JiranisokoTech.Web/
COPY tests/JiranisokoTech.Tests/*.csproj tests/JiranisokoTech.Tests/

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore JiranisokoTech.slnx

COPY . .

# Tests run inside the image build. A container that compiles but fails its own
# suite should never reach a registry, and finding that out here costs a minute
# rather than a deployment.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet test tests/JiranisokoTech.Tests/JiranisokoTech.Tests.csproj \
      --configuration $BUILD_CONFIGURATION --no-restore

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/JiranisokoTech.Web/JiranisokoTech.Web.csproj \
      --configuration $BUILD_CONFIGURATION \
      --no-restore \
      --output /app

# ---------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

# curl, purely so the container can answer a health probe about itself. The
# aspnet image ships without it, and a HEALTHCHECK that calls something absent
# reports unhealthy forever — which looks exactly like the application being
# broken. Installed before dropping to the unprivileged user.
RUN apt-get update \
    && apt-get install --no-install-recommends --yes curl \
    && rm -rf /var/lib/apt/lists/*

# Non-root. The base image ships an `app` user for exactly this; a web process
# running as root inside a container is one container escape away from being
# root on the host.
USER app

# 8080 rather than 80, because a non-root process cannot bind a privileged port.
# TLS is terminated by the reverse proxy in front of this.
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1

COPY --from=build /app .

# The orchestrator needs to know the difference between "the process is up" and
# "the process can serve requests". /health answers the first, /ready the second
# — see Program.cs.
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "JiranisokoTech.Web.dll"]
