# dotnet-alpine

A minimal ASP.NET Core 8 test application containerized on Alpine Linux. Built to exercise Dynatrace OneAgent instrumentation — it generates a continuous stream of inbound and outbound HTTP spans, CPU work, and async traces without needing external load.

## Why Alpine + musl

The app publishes with `-r linux-musl-x64 --no-self-contained` so the native process launcher is musl-linked, matching the Alpine runtime. This matters for OneAgent: the process agent injects into the target process, and a mixed glibc/musl process space breaks attachment.

## Endpoints

| Endpoint | Description |
|---|---|
| `GET /` | Health check |
| `GET /api/http` | Outbound HTTP to httpbin — exercises exit-span detection |
| `GET /api/cpu?iterations=N` | CPU loop — exercises method-level profiling |
| `GET /api/delay?ms=N` | Async delay — exercises async/await span continuations |
| `GET /api/chain` | Delay + outbound HTTP in sequence — multi-span trace |
| `GET /api/error?code=N` | Returns a non-2xx status — exercises error-rate visibility |

The app also runs a background traffic generator that fires random self-requests every 3–10 seconds, so OneAgent has a continuous stream of spans to instrument even without external load.

## Build and run

```bash
docker build -t tracetest .
docker run --rm -p 5080:5080 tracetest
```

The app listens on `http://localhost:5080`.

## Project structure

```
Dockerfile
src/
  TraceTest/
    Program.cs        # Endpoints + background traffic generator
    TraceTest.csproj
```
