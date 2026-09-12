# Runs the Linux-testable part of Ripcord: Domain, Ports, Application, Cli, Adapters.Fake.
# Ripcord.Linux.slnf excludes the WMI adapter, keeping the tested surface Linux-only.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# Matches the CI, which sets CI=true and so builds with ContinuousIntegrationBuild.
ENV CI=true

WORKDIR /src

# .git included on purpose: the build stamps the commit hash into InformationalVersion.
# Without it the stamp falls back to "unknown", which is tested but not what we want here.
COPY . .

RUN dotnet test Ripcord.Linux.slnf -c Release --logger trx
