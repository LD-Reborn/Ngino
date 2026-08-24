# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY src/Ngino.Protocol/Ngino.Protocol.csproj src/Ngino.Protocol/
COPY src/Ngino.Client/Ngino.Client.csproj src/Ngino.Client/

# Restore dependencies
RUN dotnet restore src/Ngino.Client/Ngino.Client.csproj

# Copy remaining source code
COPY src/ src/

# Build the application
FROM build AS publish
WORKDIR /src/src/Ngino.Client
RUN dotnet publish Ngino.Client.csproj -c Release -o /app/publish --no-restore

# Docker CLI stage; the client uses the docker CLI to spawn sibling
# llama.cpp containers on the host daemon (mount /var/run/docker.sock).
FROM docker:cli AS docker-cli

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Copy the docker CLI so the client can manage llama.cpp containers.
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker

# Copy published files
COPY --from=publish /app/publish .

# Log directory (override with NGINO_LOG_DIR)
RUN mkdir -p /app/Logs

ENV NGINO_LOG_DIR=/app/Logs \
    NGINO_UPSTREAM=http://localhost:11434 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

ENTRYPOINT ["dotnet", "Ngino.Client.dll"]
