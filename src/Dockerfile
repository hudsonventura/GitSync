# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/GitSync.csproj ./src/
RUN dotnet restore ./src/GitSync.csproj

COPY src/. ./src/
RUN dotnet publish ./src/GitSync.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# Install git (required for mirror operations)
RUN apt-get update && \
    apt-get install -y --no-install-recommends git && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .


ENV REPOS_PATH=/data/repos
ENV SYNC_INTERVAL_SECONDS=300

ENTRYPOINT ["dotnet", "GitSync.dll"]
