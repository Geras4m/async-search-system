# syntax=docker/dockerfile:1
#
# Search Service — gRPC host plus the background search execution engine.
#
# IMPORTANT: THE BUILD CONTEXT FOR THIS FILE IS THE REPOSITORY ROOT, NOT THIS DIRECTORY.
# This is the single most surprising thing about the Dockerfiles in this repository, so it
# is worth stating plainly. The solution uses Central Package Management and a shared
# Directory.Build.props, and this project references SearchService.Application,
# SearchService.Infrastructure and src/Shared/Shared.GrpcContracts. All of those live
# *above* src/SearchService/SearchService.Api, and Docker cannot read anything outside its
# context, so the context has to be the repository root. Every COPY path below is
# therefore relative to the repository root.
#
# docker-compose.yml already does the right thing (context: ".", dockerfile: this file).
# To build by hand, run from the repository root:
#
#   docker build -f src/SearchService/SearchService.Api/Dockerfile -t asyncsearch/searchservice .
#
# Running "docker build ." from inside this directory will fail on the very first COPY.

# ---------------------------------------------------------------------------------------
# Stage 1 — build and publish
# ---------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Restore layer caching, part 1: copy only the files that influence dependency resolution.
# These change far less often than source files, so the expensive "dotnet restore" below
# stays cached across ordinary code edits. The list is identical in all three Dockerfiles
# so the resulting layers are shared between the three images.
#
# Directory.Build.props  — the target framework and analyzer settings for every project.
# Directory.Packages.props — Central Package Management; every package version lives here.
# There is no NuGet.config in this repository: restore uses the default nuget.org feed.
COPY Directory.Build.props Directory.Packages.props ./

# The project files, each into its own directory so the relative <ProjectReference> paths
# inside them resolve. Test projects are deliberately omitted: nothing in the src graph
# references them, and leaving them out keeps this layer from churning on test-only edits.
COPY src/ApiGateway/ApiGateway.csproj                                     src/ApiGateway/
COPY src/NotificationService/NotificationService.csproj                   src/NotificationService/
COPY src/SearchService/SearchService.Api/SearchService.Api.csproj         src/SearchService/SearchService.Api/
COPY src/SearchService/SearchService.Application/SearchService.Application.csproj src/SearchService/SearchService.Application/
COPY src/SearchService/SearchService.Domain/SearchService.Domain.csproj   src/SearchService/SearchService.Domain/
COPY src/SearchService/SearchService.Infrastructure/SearchService.Infrastructure.csproj src/SearchService/SearchService.Infrastructure/
COPY src/Shared/Shared.Common/Shared.Common.csproj                        src/Shared/Shared.Common/
COPY src/Shared/Shared.EventContracts/Shared.EventContracts.csproj        src/Shared/Shared.EventContracts/
COPY src/Shared/Shared.GrpcContracts/Shared.GrpcContracts.csproj          src/Shared/Shared.GrpcContracts/

# Restore layer caching, part 2: resolve packages for this project and its project graph.
# This also pulls Grpc.Tools, whose bundled protoc generates the client and server stubs
# from src/Shared/Shared.GrpcContracts/Protos/search.proto during the publish below.
RUN dotnet restore src/SearchService/SearchService.Api/SearchService.Api.csproj

# Only now bring in the sources. Everything above this line survives a source-only change.
# .dockerignore keeps bin/, obj/ and other build residue out, which matters here: the obj/
# directories produced by the restore above must not be clobbered by host artefacts, or
# the --no-restore publish below would fail to find its assets file.
COPY . .

# UseAppHost=false skips the native launcher executable. The entrypoint invokes the managed
# assembly through the shared "dotnet" host that the runtime image already provides, so the
# apphost would be dead weight.
RUN dotnet publish src/SearchService/SearchService.Api/SearchService.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------------------------------------------------------------------------------------
# Stage 2 — runtime
# ---------------------------------------------------------------------------------------
# ASP.NET Core runtime: this service hosts gRPC over Kestrel, which needs HTTP/2 cleartext
# (h2c). The endpoint protocol is configured by the application itself, not here.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# The aspnet:8.0 image already defines a non-root user named "app". Copying with --chown
# hands ownership of the published output to it, so USER app below can read and execute
# everything under /app without any further permission fixing.
COPY --from=build --chown=app:app /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1

EXPOSE 8080

# Drop root before the process starts: nothing this service does requires elevated rights.
USER app

ENTRYPOINT ["dotnet", "SearchService.Api.dll"]
