# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore as its own layer so it is cached until .csproj changes
COPY src/TraceTest/TraceTest.csproj TraceTest/
RUN dotnet restore TraceTest/TraceTest.csproj -r linux-musl-x64

COPY src/TraceTest/ TraceTest/
WORKDIR /src/TraceTest
RUN dotnet publish -c Release -o /app/publish -r linux-musl-x64 --no-self-contained --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final

# Use HTTP on 5080 to match the customer port without certificate complexity
ENV ASPNETCORE_URLS=http://+:5080
ENV ASPNETCORE_HTTP_PORT=5080

# Alpine does not ship ICU by default; required for full .NET globalization
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Run as non-root — mirrors customer's security posture
RUN adduser --disabled-password --home /app --gecos '' nonroot \
    && chown -R nonroot /app
USER nonroot

WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 5080

ENTRYPOINT ["./TraceTest"]
