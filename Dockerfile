# Build for the Raspberry Pi (vision.md §6). Kept separate from the local
# development loop on purpose: development runs the API with `dotnet run` against
# the compose Postgres, and this image is what the Pi deploys.
#
# Multi-stage: restore/build on the SDK image, ship only the published output on
# the runtime image. The published app is self-contained-ish (framework-dependent
# ASP.NET runtime is in the runtime image), so the image carries no SDK.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project files first so restore is cached independently of the source.
COPY src/Server/MdBolsa.Server/MdBolsa.Server.csproj src/Server/MdBolsa.Server/
RUN dotnet restore src/Server/MdBolsa.Server/MdBolsa.Server.csproj

COPY src/Server/ src/Server/
RUN dotnet publish src/Server/MdBolsa.Server/MdBolsa.Server.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Never run as root: the container writes to the mounted data volume, and a
# container that owns an SSH-reachable host has no reason to be root.
USER $APP_UID

# Defaults for the Pi. Override every one of these with real values in the
# environment (see docs/deployment.md) - there are no production credentials in
# this repository, and the server refuses to start in Production without a
# connection string.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "MdBolsa.Server.dll"]
