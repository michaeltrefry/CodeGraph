FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy NuGet config and solution for restore
COPY nuget.docker.config nuget.config
COPY TC.CodeGraphApi.sln .
COPY src/TC.CodeGraphApi/TC.CodeGraphApi.csproj src/TC.CodeGraphApi/
COPY src/TC.CodeGraphApi.Console/TC.CodeGraphApi.Console.csproj src/TC.CodeGraphApi.Console/
COPY src/TC.CodeGraphApi.Models/TC.CodeGraphApi.Models.csproj src/TC.CodeGraphApi.Models/
COPY src/TC.CodeGraphApi.Services/TC.CodeGraphApi.Services.csproj src/TC.CodeGraphApi.Services/
COPY src/TC.CodeGraphApi.Data/TC.CodeGraphApi.Data.csproj src/TC.CodeGraphApi.Data/
COPY src/TC.CodeGraphApi.Data.Neo4j/TC.CodeGraphApi.Data.Neo4j.csproj src/TC.CodeGraphApi.Data.Neo4j/
COPY src/TC.CodeGraphJobs/TC.CodeGraphJobs.csproj src/TC.CodeGraphJobs/
COPY src/TC.CodeGraphApi.Extractors.CSharp/TC.CodeGraphApi.Extractors.CSharp.csproj src/TC.CodeGraphApi.Extractors.CSharp/
COPY src/TC.CodeGraphApi.Extractors.TypeScript/TC.CodeGraphApi.Extractors.TypeScript.csproj src/TC.CodeGraphApi.Extractors.TypeScript/
COPY src/TC.CodeGraphApi.Extractors.Sql/TC.CodeGraphApi.Extractors.Sql.csproj src/TC.CodeGraphApi.Extractors.Sql/
COPY src/TC.CodeGraphApi.Extractors.ColdFusion/TC.CodeGraphApi.Extractors.ColdFusion.csproj src/TC.CodeGraphApi.Extractors.ColdFusion/
COPY src/TC.CodeGraphApi.Extractors.Ansible/TC.CodeGraphApi.Extractors.Ansible.csproj src/TC.CodeGraphApi.Extractors.Ansible/
COPY src/TC.CodeGraphApi.Extractors.Terraform/TC.CodeGraphApi.Extractors.Terraform.csproj src/TC.CodeGraphApi.Extractors.Terraform/
COPY src/TC.CodeGraphApi.Tests/TC.CodeGraphApi.Tests.csproj src/TC.CodeGraphApi.Tests/
COPY src/TC.CodeGraphJobs.Tests/TC.CodeGraphJobs.Tests.csproj src/TC.CodeGraphJobs.Tests/

RUN dotnet restore

# Copy everything and publish
COPY src/ src/
RUN dotnet publish src/TC.CodeGraphApi/TC.CodeGraphApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS runtime
WORKDIR /app

# Install git, ssh, Node.js, Mono (for legacy .NET Framework nuget restore), and Consul deps
RUN apt-get update && apt-get install -y --no-install-recommends \
    git openssh-client curl unzip nodejs npm mono-complete && rm -rf /var/lib/apt/lists/* && \
    curl -fsSL https://dist.nuget.org/win-x86-commandline/latest/nuget.exe -o /usr/local/bin/nuget.exe && \
    printf '#!/bin/sh\nmono /usr/local/bin/nuget.exe "$@"\n' > /usr/local/bin/nuget && \
    chmod +x /usr/local/bin/nuget

# Install .NET Framework reference assemblies so Roslyn can analyze legacy Framework projects.
# 1) Restore the ref-assemblies NuGet package into the global cache
# 2) Place a Directory.Build.props at /repos/cache so MSBuild picks up FrameworkPathOverride
#    for any .NET Framework project cloned under that path
RUN dotnet new console -n _fxref -o /tmp/_fxref --no-restore && \
    dotnet add /tmp/_fxref/_fxref.csproj package Microsoft.NETFramework.ReferenceAssemblies --version 1.0.3 --no-restore && \
    dotnet restore /tmp/_fxref/_fxref.csproj && \
    rm -rf /tmp/_fxref

RUN mkdir -p /repos/cache
COPY docker/Directory.Build.props /repos/cache/Directory.Build.props

# Install older SDKs needed by MSBuildWorkspace/Roslyn to analyze target repos
RUN dotnet new globaljson 2>/dev/null; rm -f global.json && \
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    chmod +x /tmp/dotnet-install.sh && \
    /tmp/dotnet-install.sh --channel 6.0 --install-dir /usr/share/dotnet && \
    /tmp/dotnet-install.sh --channel 7.0 --install-dir /usr/share/dotnet && \
    /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet && \
    rm /tmp/dotnet-install.sh

# Install Consul
RUN curl -fsSL https://releases.hashicorp.com/consul/1.20.6/consul_1.20.6_linux_amd64.zip -o /tmp/consul.zip && \
    unzip /tmp/consul.zip -d /usr/local/bin/ && \
    rm /tmp/consul.zip && \
    mkdir -p /etc/consul.d /etc/consul/ssl /consul/data

# Copy Consul config and certs
COPY consul/config.json /etc/consul.d/config.json
COPY consul/ssl/ /etc/consul/ssl/

# Copy ts-extractor sidecar
COPY tools/ts-extractor/package.json tools/ts-extractor/package-lock.json /app/tools/ts-extractor/
RUN cd /app/tools/ts-extractor && npm ci --production
COPY tools/ts-extractor/dist/ /app/tools/ts-extractor/dist/

COPY --from=build /app/publish .
COPY nuget.config /root/.nuget/NuGet/NuGet.Config

# Startup script: launch Consul agent then the app
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENV ASPNETCORE_ENVIRONMENT=Staging
ENV TC_COLOCATION=Office
ENV ASPNETCORE_URLS=http://+:5037
EXPOSE 5037

ENTRYPOINT ["/app/entrypoint.sh"]
