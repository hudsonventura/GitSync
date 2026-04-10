# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY *.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# Install git (required for mirror operations)
RUN apt-get update && \
    apt-get install -y --no-install-recommends git && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Create default repos directory
RUN mkdir -p /data/repos

ENV REPOS_PATH=/data/repos
ENV SYNC_INTERVAL_SECONDS=300

ENTRYPOINT ["dotnet", "GitSync.dll"]
