# One Dockerfile for every NoiseCut service. Pick the service with a build argument:
#   docker build --build-arg SERVICE=Engine -t noisecut-engine .
# Multi-stage: the big SDK image compiles; only the small runtime image ships.
# (M4 hardens this further: chiseled base image, SBOM labels, SOURCE_REVISION_ID.)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /src

# Copy only the project files first and restore. Docker caches this layer, so editing a .cs file
# doesn't re-download every NuGet package on the next build.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/NoiseCut.Core/NoiseCut.Core.csproj src/NoiseCut.Core/
COPY src/NoiseCut.Infrastructure/NoiseCut.Infrastructure.csproj src/NoiseCut.Infrastructure/
COPY src/NoiseCut.${SERVICE}/NoiseCut.${SERVICE}.csproj src/NoiseCut.${SERVICE}/
RUN dotnet restore src/NoiseCut.${SERVICE}/NoiseCut.${SERVICE}.csproj

COPY src/ src/
RUN dotnet publish src/NoiseCut.${SERVICE}/NoiseCut.${SERVICE}.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG SERVICE
WORKDIR /app
COPY --from=build /app .
ENV SERVICE_DLL=NoiseCut.${SERVICE}.dll \
    DOTNET_gcServer=0
# The base image ships a non-root "app" user; never run services as root.
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
