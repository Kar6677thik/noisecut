# One Dockerfile for every NoiseCut service. Pick the service with a build argument:
#   docker build --build-arg SERVICE=Engine --build-arg GIT_SHA=$(git rev-parse HEAD) -t noisecut-engine .
#
# Multi-stage: the big SDK image compiles; only the small runtime image ships.
# Runtime = Ubuntu "chiseled" ASP.NET image: no shell, no package manager, non-root user by default. Less inside the image
# means less to patch and less for an attacker to use.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /src

# Copy only the project files first and restore. Docker caches this layer, so editing a .cs file
# doesn't re-download every NuGet package on the next build.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/NoiseCut.Core/NoiseCut.Core.csproj src/NoiseCut.Core/
COPY src/NoiseCut.Infrastructure/NoiseCut.Infrastructure.csproj src/NoiseCut.Infrastructure/
COPY src/NoiseCut.${SERVICE}/NoiseCut.${SERVICE}.csproj src/NoiseCut.${SERVICE}/
RUN dotnet restore src/NoiseCut.${SERVICE}/NoiseCut.${SERVICE}.csproj --runtime linux-x64

COPY src/ src/
RUN dotnet publish src/NoiseCut.${SERVICE}/NoiseCut.${SERVICE}.csproj -c Release -o /app --no-restore \
        --runtime linux-x64 --self-contained false \
    # The chiseled image has no shell, so the entrypoint can't be "sh -c dotnet $DLL". Instead we ship the app's native
    # launcher (apphost) under one fixed name. It already knows which .dll to run.
    && mv /app/NoiseCut.${SERVICE} /app/service

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
ARG SERVICE
ARG GIT_SHA=unknown
LABEL org.opencontainers.image.source="https://github.com/Kar6677thik/noisecut" \
      org.opencontainers.image.revision="${GIT_SHA}" \
      org.opencontainers.image.title="noisecut-${SERVICE}"
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_gcServer=0 \
    SOURCE_REVISION_ID=${GIT_SHA}
# The chiseled image runs as the non-root "app" user (uid 1654) already.
ENTRYPOINT ["/app/service"]
