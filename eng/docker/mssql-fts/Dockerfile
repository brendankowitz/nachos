# SQL Server 2025 with Full-Text Search, for the M3 hybrid-search work. M1 runs the stock image (see Nachos.AppHost);
# this recipe is built and verified now so M3 can adopt it without surprises.
FROM mcr.microsoft.com/mssql/server:2025-latest

# The base image runs as the unprivileged mssql user; package installation needs root.
USER root

# The base image trusts the Microsoft signing key (microsoft-prod.gpg) and has the generic microsoft-prod source, but
# mssql-server-fts is published only in the mssql-server-2025 repository, so that repository is added here with the same key.
RUN echo "deb [arch=amd64,arm64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/mssql-server-2025 noble main" \
        > /etc/apt/sources.list.d/mssql-server-2025.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends mssql-server-fts \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

USER mssql
