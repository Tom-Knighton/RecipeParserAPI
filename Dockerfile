# Use the official .NET 10.0 SDK image for build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY RecipeParser.API/*.csproj ./RecipeParser.API/
COPY RecipeParser.Application/*.csproj ./RecipeParser.Application/
COPY RecipeParser.Domain/*.csproj ./RecipeParser.Domain/
COPY RecipeParser.Infrastructure/*.csproj ./RecipeParser.Infrastructure/
RUN dotnet restore ./RecipeParser.API/RecipeParser.API.csproj

# --- Node install & npm ci ---
ARG NODE_DIR=RecipeParser.Application/Services/Node
COPY ${NODE_DIR}/package*.json /tmp/node/
RUN apt-get update \
 && apt-get install -y --no-install-recommends nodejs npm ca-certificates \
 && if [ ! -e /usr/bin/node ] && [ -e /usr/bin/nodejs ]; then ln -s /usr/bin/nodejs /usr/bin/node; fi \
 && npm ci --prefix /tmp/node --omit=dev \
 && rm -rf /var/lib/apt/lists/*
# --- end Node prep ---

# Copy the rest of the source code
COPY . .

# Build and publish the app
RUN dotnet publish ./RecipeParser.API/RecipeParser.API.csproj -c Release -o /out

# Copy Node.js files to the output directory
RUN mkdir -p /out/Node \
 && cp -a /tmp/node/node_modules /out/Node/ \
 && cp -a ${NODE_DIR}/*.js /out/Node/ 2>/dev/null || true \
 && cp -a ${NODE_DIR}/*.cjs /out/Node/ 2>/dev/null || true

# Use the official .NET 10.0 runtime image for the final container
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Install Node
ENV NODE_ENV=production
RUN apt-get update \
 && apt-get install -y --no-install-recommends nodejs ca-certificates \
 && if [ ! -e /usr/bin/node ] && [ -e /usr/bin/nodejs ]; then ln -s /usr/bin/nodejs /usr/bin/node; fi \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /out ./

# Install PowerShell (pwsh)
ARG POWERSHELL_VERSION=7.5.4
RUN set -eux; \
    arch="$(dpkg --print-architecture)"; \
    apt-get update; \
    apt-get install -y --no-install-recommends wget ca-certificates tar; \
    if [ "$arch" = "amd64" ]; then \
      wget -q -O /tmp/powershell.deb \
        "https://github.com/PowerShell/PowerShell/releases/download/v${POWERSHELL_VERSION}/powershell_${POWERSHELL_VERSION}-1.deb_amd64.deb"; \
      dpkg -i /tmp/powershell.deb || apt-get install -y --no-install-recommends -f; \
      rm -f /tmp/powershell.deb; \
      chmod 755 /usr/bin/pwsh; \
    elif [ "$arch" = "arm64" ]; then \
      mkdir -p /opt/microsoft/powershell/7; \
      wget -q -O /tmp/powershell.tar.gz \
        "https://github.com/PowerShell/PowerShell/releases/download/v${POWERSHELL_VERSION}/powershell-${POWERSHELL_VERSION}-linux-arm64.tar.gz"; \
      tar -xzf /tmp/powershell.tar.gz -C /opt/microsoft/powershell/7; \
      rm -f /tmp/powershell.tar.gz; \
      chmod -R a+rX /opt/microsoft/powershell/7; \
      chmod 755 /opt/microsoft/powershell/7/pwsh; \
      ln -sf /opt/microsoft/powershell/7/pwsh /usr/bin/pwsh; \
    else \
      echo "Unsupported arch: $arch" >&2; exit 1; \
    fi; \
    rm -rf /var/lib/apt/lists/*

# Install Playwright browsers + OS deps
RUN pwsh ./playwright.ps1 install chromium --with-deps

# Expose port 8080
ENV ASPNETCORE_URLS=http://*:8080
EXPOSE 8080

# Set the entrypoint
ENTRYPOINT ["dotnet", "RecipeParser.API.dll"]
