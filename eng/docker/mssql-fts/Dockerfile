# SQL Server 2025 with Full-Text Search, for the M3 hybrid-search work. M1 runs the stock image (see Nachos.AppHost);
# this recipe is built and verified now so M3 can adopt it without surprises.
#
# Bump the update tag and the package version together; both must name the same build (CU9 = 17.0.5005.3).
ARG MSSQL_UPDATE=CU9
ARG MSSQL_PACKAGE_VERSION=17.0.5005.3-1

# Stage 1: install the package with apt. The base image ships the engine without registering it with dpkg, so apt must
# install the engine package as well (hence the exact version pin: it overwrites /opt/mssql with the build it replaces).
# The throwaway stage keeps that second copy of the engine out of the final image, which would otherwise double in size.
FROM mcr.microsoft.com/mssql/server:2025-${MSSQL_UPDATE}-ubuntu-24.04 AS fts
ARG MSSQL_PACKAGE_VERSION
USER root
# The base image trusts the Microsoft signing key (microsoft-prod.gpg) and has the generic microsoft-prod source, but
# mssql-server-fts is published only in the mssql-server-2025 repository, so that repository is added here with the same key.
RUN echo "deb [arch=amd64,arm64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/mssql-server-2025 noble main" \
        > /etc/apt/sources.list.d/mssql-server-2025.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        mssql-server=${MSSQL_PACKAGE_VERSION} \
        mssql-server-fts=${MSSQL_PACKAGE_VERSION} \
    && rm -rf /var/lib/apt/lists/*

# Stage 2: the base image plus exactly the files the mssql-server-fts package installs (see `dpkg -L mssql-server-fts`).
FROM mcr.microsoft.com/mssql/server:2025-${MSSQL_UPDATE}-ubuntu-24.04
COPY --from=fts --chown=root:root /opt/mssql/lib/sqlservr.fts.sfp /opt/mssql/lib/sqlservr.fts.sfp
COPY --from=fts --chown=root:root /opt/mssql/misc/semanticsdb.bak /opt/mssql/misc/semanticsdb.bak
USER mssql
