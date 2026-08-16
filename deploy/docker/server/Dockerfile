# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY src/Ngino.Protocol/Ngino.Protocol.csproj src/Ngino.Protocol/
COPY src/Ngino.Server/Ngino.Server.csproj src/Ngino.Server/

# Restore dependencies
RUN dotnet restore src/Ngino.Server/Ngino.Server.csproj

# Copy remaining source code
COPY src/ src/

# Build the application
FROM build AS publish
WORKDIR /src/src/Ngino.Server
RUN dotnet publish Ngino.Server.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install dependencies (SQLite, MySQL for Elmah)
RUN apt-get update && apt-get install -y \
    libsqlite3-dev \
    libmysqlclient-dev \
    && rm -rf /var/lib/apt/lists/*

# Create data directory for SQLite databases
RUN mkdir -p /app/App_Data

# Copy published files including wwwroot
COPY --from=publish /app/publish .

# Expose port
EXPOSE 8080

ENTRYPOINT ["dotnet", "Ngino.Server.dll"]
