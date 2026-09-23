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

# The measurement harness. It is in the solution, so restore needs its project file even
# though nothing in the image runs it — and when it was added without this line the image
# stopped building entirely: `dotnet restore` failed on a missing .csproj, and
# `compose up --build` went on serving the previous image rather than stopping. The local
# build and the whole test suite stayed green throughout, which is why this was found by
# looking at a page in a browser and noticing it was the old one.
COPY tools/JiranisokoTech.ScaleCheck/*.csproj tools/JiranisokoTech.ScaleCheck/

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore JiranisokoTech.slnx

COPY . .

# Restored a second time, now that the whole source tree is here, and this is
# not redundant. The restore above ran when only .csproj files existed, and at
# that moment the web project appears to contain no Razor components — so the
# SDK does not add the implicit Microsoft.AspNetCore.App.Internal.Assets
# reference that carries blazor.web.js. Everything downstream passes
# --no-restore, which would then lock that incomplete assets file in: the image
# builds, the application starts, every page renders, and the framework script
# 404s into the deny-by-default policy and comes back as a redirect to the sign
# in page. Nothing is interactive yet so nothing visibly breaks, which is what
# makes it worth a paragraph — the first component to ask for @rendermode would
# simply not work, in the container only, having worked on the machine it was
# written on.
#
# It costs a few seconds: the packages were downloaded above and are still in
# the cache mount, so this resolves rather than downloads.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore JiranisokoTech.slnx

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

# The directories written to at runtime — the signing key ring, attachments and
# CVs — created and handed to the application's user before that user takes
# over.
#
# The order matters and is not obvious. A named volume mounted here takes its
# ownership from whatever the image has at this path, so if the directory does
# not exist — or exists owned by root — the volume is root-owned and the
# non-root process cannot write to it. The application then starts, reports
# healthy, serves the sign-in page's markup, and throws on the antiforgery
# token, because the first thing that needs a key is the first form anybody
# opens. Which is to say: a 500 on the page everyone lands on, from a directory
# permission.
RUN mkdir -p /keys /documents /cvs && chown app:app /keys /documents /cvs

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
