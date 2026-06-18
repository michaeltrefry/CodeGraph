FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy NuGet config and project files for restore
COPY nuget.docker.config nuget.config
COPY CodeGraph.sln .
COPY src/CodeGraph.Api/CodeGraph.Api.csproj src/CodeGraph.Api/
COPY src/CodeGraph.Host.Shared/CodeGraph.Host.Shared.csproj src/CodeGraph.Host.Shared/
COPY src/CodeGraph.Indexer.Client/CodeGraph.Indexer.Client.csproj src/CodeGraph.Indexer.Client/
COPY src/CodeGraph.Memory.Client/CodeGraph.Memory.Client.csproj src/CodeGraph.Memory.Client/
COPY src/CodeGraph.Mcp.Hub/CodeGraph.Mcp.Hub.csproj src/CodeGraph.Mcp.Hub/
COPY src/CodeGraph.Models/CodeGraph.Models.csproj src/CodeGraph.Models/
COPY src/CodeGraph.Services/CodeGraph.Services.csproj src/CodeGraph.Services/
COPY src/CodeGraph.Data/CodeGraph.Data.csproj src/CodeGraph.Data/
COPY src/CodeGraph.Data.Neo4j/CodeGraph.Data.Neo4j.csproj src/CodeGraph.Data.Neo4j/
COPY src/CodeGraph.Data.MariaDb/CodeGraph.Data.MariaDb.csproj src/CodeGraph.Data.MariaDb/
COPY src/CodeGraph.Jobs/CodeGraph.Jobs.csproj src/CodeGraph.Jobs/
COPY src/CodeGraph.Extractors.Ansible/CodeGraph.Extractors.Ansible.csproj src/CodeGraph.Extractors.Ansible/
COPY src/CodeGraph.Extractors.CSharp/CodeGraph.Extractors.CSharp.csproj src/CodeGraph.Extractors.CSharp/
COPY src/CodeGraph.Extractors.ColdFusion/CodeGraph.Extractors.ColdFusion.csproj src/CodeGraph.Extractors.ColdFusion/
COPY src/CodeGraph.Extractors.Rust/CodeGraph.Extractors.Rust.csproj src/CodeGraph.Extractors.Rust/
COPY src/CodeGraph.Extractors.TypeScript/CodeGraph.Extractors.TypeScript.csproj src/CodeGraph.Extractors.TypeScript/
COPY src/CodeGraph.Extractors.Sql/CodeGraph.Extractors.Sql.csproj src/CodeGraph.Extractors.Sql/
COPY src/CodeGraph.Extractors.Terraform/CodeGraph.Extractors.Terraform.csproj src/CodeGraph.Extractors.Terraform/
COPY src/CodeGraph.Extractors.TreeSitter/CodeGraph.Extractors.TreeSitter.csproj src/CodeGraph.Extractors.TreeSitter/

RUN dotnet restore src/CodeGraph.Api/CodeGraph.Api.csproj

# Copy everything and publish
COPY src/ src/
RUN dotnet publish src/CodeGraph.Api/CodeGraph.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
WORKDIR /app
ARG TARGETARCH

# Install git, ssh, Node.js, and Mono for legacy .NET Framework NuGet restore
RUN apt-get update && apt-get install -y --no-install-recommends \
    git openssh-client curl unzip nodejs npm mono-complete && rm -rf /var/lib/apt/lists/* && \
    curl -fsSL https://dist.nuget.org/win-x86-commandline/latest/nuget.exe -o /usr/local/bin/nuget.exe && \
    printf '#!/bin/sh\nmono /usr/local/bin/nuget.exe "$@"\n' > /usr/local/bin/nuget && \
    chmod +x /usr/local/bin/nuget

# .NET Core 2.1 SDK depends on OpenSSL 1.1, which is no longer shipped in the
# Ubuntu 24.04 base image used by the .NET 10 container tags. Pull the
# compatibility package from Ubuntu 20.04 security updates so exact global.json
# pins like 2.1.802 can still restore/analyze older repos.
RUN ubuntu_mirror="http://ports.ubuntu.com/ubuntu-ports"; \
    if [ "${TARGETARCH:-$(dpkg --print-architecture)}" = "amd64" ]; then \
        ubuntu_mirror="http://archive.ubuntu.com/ubuntu"; \
    fi; \
    echo "deb ${ubuntu_mirror} focal-security main" > /etc/apt/sources.list.d/focal-libssl.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends libssl1.1 && \
    rm -f /etc/apt/sources.list.d/focal-libssl.list && \
    rm -rf /var/lib/apt/lists/*

# Install .NET Framework reference assemblies so Roslyn can analyze legacy Framework projects.
# 1) Restore the ref-assemblies NuGet package into the global cache
# 2) Place a Directory.Build.props at /repos so MSBuild picks up FrameworkPathOverride
#    for any .NET Framework project cloned under that path
RUN dotnet new console -n _fxref -o /tmp/_fxref --no-restore && \
    dotnet add /tmp/_fxref/_fxref.csproj package Microsoft.NETFramework.ReferenceAssemblies --version 1.0.3 --no-restore && \
    dotnet restore /tmp/_fxref/_fxref.csproj && \
    rm -rf /tmp/_fxref

RUN mkdir -p /repos
COPY docker/Directory.Build.props /repos/Directory.Build.props

# Install compatibility SDKs needed by MSBuildWorkspace/Roslyn to analyze target repos.
# The base sdk:10.0 image already provides .NET 10; add older channels plus .NET 9
# so repos pinned to earlier global.json versions can still restore and load in-container.
RUN dotnet new globaljson 2>/dev/null; rm -f global.json && \
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    chmod +x /tmp/dotnet-install.sh && \
    /tmp/dotnet-install.sh --version 2.1.802 --install-dir /usr/share/dotnet && \
    /tmp/dotnet-install.sh --channel 6.0 --install-dir /usr/share/dotnet && \
    /tmp/dotnet-install.sh --channel 7.0 --install-dir /usr/share/dotnet && \
    /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet && \
    /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet && \
    /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet && \
    rm /tmp/dotnet-install.sh

# Copy ts-extractor sidecar
COPY tools/ts-extractor/package.json tools/ts-extractor/package-lock.json /app/tools/ts-extractor/
RUN cd /app/tools/ts-extractor && npm ci --omit=dev
COPY tools/ts-extractor/dist/ /app/tools/ts-extractor/dist/

COPY --from=build /app/publish .
COPY sql/migrations /app/sql/migrations
COPY nuget.docker.config /root/.nuget/NuGet/NuGet.Config
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENV ASPNETCORE_ENVIRONMENT=Staging
ENV ASPNETCORE_URLS=http://+:5037
EXPOSE 5037

ENTRYPOINT ["/app/entrypoint.sh"]
