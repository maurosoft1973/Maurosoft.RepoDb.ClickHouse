<!--
  This is the SOURCE file for README.md.

  README.md is generated from this template by generate-readme.ps1, which injects the per-class
  coverage table between the COVERAGE:START / COVERAGE:END HTML-comment markers found further down
  this file. The CI pipeline (.github/workflows/ci.yml, "Unit Tests + Integration Tests" job) runs
  that script automatically on every push to main and commits the regenerated README.md back to
  the repo.

  ALWAYS edit README.tpl, never README.md directly -- direct edits to README.md are overwritten on
  the next regeneration. To preview the generated output locally:

    pwsh ./generate-readme.ps1 -CoverageReportPath coveragereport/SummaryGithub.md
-->
# Maurosoft.RepoDb.ClickHouse

[![NuGet Version](https://img.shields.io/nuget/v/Maurosoft.RepoDb.ClickHouse.svg?logo=nuget)](https://www.nuget.org/packages/Maurosoft.RepoDb.ClickHouse/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Maurosoft.RepoDb.ClickHouse.svg?logo=nuget)](https://www.nuget.org/packages/Maurosoft.RepoDb.ClickHouse/)
[![Build](https://github.com/maurosoft1973/Maurosoft.RepoDb.ClickHouse/actions/workflows/ci.yml/badge.svg)](https://github.com/maurosoft1973/Maurosoft.RepoDb.ClickHouse/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/maurosoft1973/Maurosoft.RepoDb.ClickHouse/branch/main/graph/badge.svg)](https://codecov.io/gh/maurosoft1973/Maurosoft.RepoDb.ClickHouse)
[![License: MIT](https://img.shields.io/github/license/maurosoft1973/Maurosoft.RepoDb.ClickHouse)](LICENSE.md)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)

A [RepoDb](https://repodb.net) extension that adds first-class [ClickHouse](https://clickhouse.com/) support, built on top of the official ADO.NET provider, [`ClickHouse.Driver`](https://github.com/ClickHouse/clickhouse-cs).

It wires up RepoDb's `IDbSetting`, `IDbHelper`, and `IStatementBuilder` for ClickHouse's SQL dialect, adapts RepoDb's ADO.NET parameter conventions to ClickHouse.Driver's native substitution syntax, and adds a native RowBinary bulk-insert path for high-throughput ingestion — so you get RepoDb's familiar, low-ceremony API (`InsertAsync`, `QueryAsync`, `MergeAsync`, …) working correctly against a database engine that was never designed around classic OLTP semantics.

## Features

- Full RepoDb CRUD support (`InsertAsync`, `QueryAsync`, `UpdateAsync`, `DeleteAsync`, `MergeAsync`, `CountAsync`, `ExistsAsync`, …) targeting ClickHouse
- Correct SQL generation for ClickHouse's dialect: backtick-quoted identifiers, `LIMIT`/`OFFSET` pagination, `ALTER TABLE ... UPDATE`/`DELETE` mutations, `INSERT`-based upsert fallback for `MERGE`
- Transparent ADO.NET parameter adaptation — write ordinary RepoDb expressions; the library rewrites parameters into ClickHouse.Driver's native `{name:Type}` substitution syntax under the hood
- Native RowBinary bulk insert (`BulkInsertAsync`) for batches in the hundreds of thousands of rows, with streaming (`IAsyncEnumerable<T>`) support
- Schema introspection via `system.columns`, with ClickHouse-to-.NET type mapping (`DateTime64`, `LowCardinality`, `UUID`, `Array(T)`, `Nullable(T)`, …)
- A typed `ClickHouseRepository<TEntity>` base class for a clean-architecture-friendly repository pattern

## Installation

```bash
dotnet add package Maurosoft.RepoDb.ClickHouse
```

or via `PackageReference`:

```xml
<PackageReference Include="Maurosoft.RepoDb.ClickHouse" Version="1.1.0" />
```

### Requirements

| | |
|---|---|
| Target framework | .NET 9.0 |
| [`RepoDb`](https://www.nuget.org/packages/RepoDb) | 1.15.1 or later |
| [`ClickHouse.Driver`](https://www.nuget.org/packages/ClickHouse.Driver) | 1.3.0 or later |
| ClickHouse server | 22.8+ (lightweight `DELETE` support required) |

Both dependencies are pulled in automatically by the NuGet package — no extra `PackageReference` entries needed for them.

## Getting a ClickHouse server (Docker)

The fastest way to get a ClickHouse instance for local development is Docker:

```bash
docker run -d --name clickhouse \
  -p 8123:8123 -p 9000:9000 \
  -e CLICKHOUSE_DB=mydb \
  -e CLICKHOUSE_USER=default \
  -e CLICKHOUSE_PASSWORD=changeme \
  clickhouse/clickhouse-server:24.10-alpine
```

Or with `docker-compose.yml`:

```yaml
services:
  clickhouse:
    image: clickhouse/clickhouse-server:24.10-alpine
    ports:
      - "8123:8123"   # HTTP interface, used by ClickHouse.Driver
      - "9000:9000"   # native TCP interface
    environment:
      CLICKHOUSE_DB: mydb
      CLICKHOUSE_USER: default
      CLICKHOUSE_PASSWORD: changeme
    volumes:
      - clickhouse-data:/var/lib/clickhouse

volumes:
  clickhouse-data:
```

```bash
docker compose up -d
```

The corresponding connection string for the examples below:

```csharp
const string ConnectionString =
    "Host=localhost;Port=8123;Database=mydb;Username=default;Password=changeme;Protocol=http";
```

If your test or CI environment can spin up containers on demand, [Testcontainers for .NET](https://dotnet.testcontainers.org/) (specifically the [`Testcontainers.ClickHouse`](https://www.nuget.org/packages/Testcontainers.ClickHouse) module) is a good fit for integration tests — this is exactly what this library's own test suite uses.

## Setup

Register ClickHouse support with RepoDb once, at application startup:

```csharp
using RepoDb;
using RepoDb.ClickHouse;

// Program.cs — once, before any RepoDb call
GlobalConfiguration.Setup().UseClickHouse();
```

## Quick start

> [!IMPORTANT]
> Always use `RepoDbClickHouseConnection` — **not** `ClickHouse.Driver.ADO.ClickHouseConnection`
> directly — when calling RepoDb extension methods (`InsertAsync`, `QueryAsync`, `UpdateAsync`, …).
> RepoDb resolves the registered `IDbSetting`/`IDbHelper`/`IStatementBuilder` by the *compile-time*
> type of the connection variable, and `ClickHouse.Driver` 1.3+ no longer understands the `@name`
> ADO.NET parameter placeholders RepoDb generates — it requires its own `{name:Type}` substitution
> syntax. `RepoDbClickHouseConnection` rewrites every command automatically before execution; the raw
> driver connection does not. This is not required for `BulkInsertAsync`, which uses the RowBinary
> binary protocol instead of parameterized SQL text.

```csharp
using RepoDb;
using RepoDb.ClickHouse;
using RepoDb.Attributes;

[Map("events")]
public class Event
{
    [Primary]
    public Guid Id { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

await using var conn = new RepoDbClickHouseConnection(ConnectionString);

// Insert
await conn.InsertAsync(new Event { Id = Guid.NewGuid(), Status = "active", CreatedAt = DateTime.UtcNow });

// Query with a LINQ-style expression
var active = await conn.QueryAsync<Event>(e => e.Status == "active");

// Batched insert (parameterized ADO.NET path, auto-rewritten for ClickHouse)
await conn.InsertAllAsync(events, batchSize: 5_000);

// High-throughput native bulk insert (RowBinary protocol)
await conn.BulkInsertAsync(events, batchSize: 100_000);

// Raw SQL for ClickHouse-specific query features
var result = await conn.ExecuteQueryAsync<Event>(
    "SELECT * FROM events WHERE toDate(CreatedAt) = today()");
```

### Bulk insert with tuning options

`BulkInsertAsync` uses ClickHouse's native RowBinary protocol and is the recommended path once you are
inserting more than roughly 100,000 rows at a time:

```csharp
using RepoDb.ClickHouse.Bulk;

var result = await conn.BulkInsertAsync(events, ClickHouseBulkInsertOptions.HighThroughput with
{
    BatchSize = 250_000
});

Console.WriteLine(result);
// BulkInsert 'events': 1,000,000 rows in 4 batches, 6.12s (163,398 rows/s)
```

It also accepts an `IAsyncEnumerable<T>` for streaming ingestion from a file, queue, or upstream query
without buffering the whole source in memory:

```csharp
await conn.BulkInsertAsync(ReadEventsFromKafkaAsync(), ClickHouseBulkInsertOptions.Default);
```

## Repository pattern

`ClickHouseRepository<TEntity>` gives you a typed base class for a clean-architecture-friendly
repository, on top of the same `RepoDbClickHouseConnection`-backed pipeline:

```csharp
public interface IEventRepository
{
    Task<IEnumerable<Event>> GetByStatusAsync(string status);
    Task UpsertAsync(Event ev);
}

public class EventRepository : ClickHouseRepository<Event>, IEventRepository
{
    public EventRepository(string connectionString) : base(connectionString) { }

    public Task<IEnumerable<Event>> GetByStatusAsync(string status)
        => QueryAsync(e => e.Status == status);

    public Task UpsertAsync(Event ev)
        => MergeAsync(ev); // translates to an INSERT against a ReplacingMergeTree table
}
```

## Architecture

| RepoDb contract | Implementation | Responsibility |
|---|---|---|
| `IDbConnection` | `RepoDbClickHouseConnection` | Rewrites `@name` → `{name:Type}` before every execution; returns a no-op transaction |
| `IDbSetting` | `ClickHouseDbSetting` | Backtick quoting, `@` parameter prefix, no SQL-standard schema concept |
| `IDbHelper` | `ClickHouseDbHelper` | Schema introspection via `system.columns` |
| `IStatementBuilder` | `ClickHouseStatementBuilder` | Generates SQL for ClickHouse's dialect |
| Repository base | `ClickHouseRepository<T>` | Extends `BaseRepository<T, RepoDbClickHouseConnection>` |
| Bulk engine | `ClickHouseBulkInserter<T>` | Native RowBinary insert via `ClickHouseClient.InsertBinaryAsync` |

## ClickHouse-specific behavior

### UPDATE (mutations)

ClickHouse has no classic `UPDATE` statement. The library generates a mutation instead:

```sql
ALTER TABLE `table` UPDATE col = @col WHERE ...
```

**Mutations run asynchronously** on the server — the update is not guaranteed to be visible to reads
that immediately follow it. For upsert-style workloads, prefer `ReplacingMergeTree` combined with
`MergeAsync` (which issues an `INSERT`) instead of updating rows in place.

### DELETE

Uses ClickHouse's **lightweight delete** (available since 22.8):

```sql
DELETE FROM `table` WHERE ...
```

On `MergeTree`-family tables, matching rows are marked for deletion and physically removed on the next
background merge.

### MERGE / upsert

ClickHouse has no `MERGE` statement. `MergeAsync` falls back to a plain `INSERT`, which only produces
correct upsert semantics against:

- `ReplacingMergeTree` — deduplicated on merge, keeping the most recently inserted row per sort key
- `CollapsingMergeTree` — logical deletion via sign-column rows

Using it against a plain `MergeTree` table simply inserts a duplicate row.

### Transactions

ClickHouse has **no ACID transactions** — every statement is auto-committed by the engine.
`RepoDbClickHouseConnection.BeginTransaction()` returns a no-op transaction instead of throwing:
`Commit()`/`Rollback()` are silently ignored. This is required because RepoDb always opens an implicit
transaction around its batch operations (`InsertAllAsync`, `DeleteAllAsync`, `UpdateAllAsync`, …).

### Type mapping

The schema resolver maps native ClickHouse types to their .NET equivalents, including
`DateTime`/`DateTime64`, `Date`/`Date32` → `DateOnly`, `UUID` → `Guid`, and unwraps
`Nullable(T)`/`LowCardinality(T)` wrappers transparently.

## Performance tips

- Prefer `BulkInsertAsync` over `InsertAllAsync` once batches exceed roughly 100,000 rows — the native RowBinary protocol has significantly less overhead than parameterized SQL text.
- When using `InsertAllAsync`, use `batchSize >= 1,000`.
- Filter or order on primary-key/sort-key columns wherever possible — ClickHouse's `MergeTree` engines are built around efficient range scans on the sort key.
- For complex OLAP queries (aggregations, `ARRAY JOIN`, window functions), prefer `ExecuteQueryAsync` with raw SQL over composing an equivalent LINQ expression.
- Avoid unfiltered `QueryAllAsync` on large tables — always add a `WHERE` clause or a `LIMIT`.

## Continuous integration

Every push to `main` or `develop` runs [`.github/workflows/ci.yml`](.github/workflows/ci.yml):

| Job | Runs on | Does |
|---|---|---|
| **Build** | `main`, `develop` | Restores and builds the solution in `Release`, computing the package version from [`version.json`](version.json) via [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning). |
| **Unit Tests + Integration Tests (Testcontainers)** | `main`, `develop` | Runs the full test suite — including the [`Testcontainers.ClickHouse`](https://www.nuget.org/packages/Testcontainers.ClickHouse)-backed integration tests, which spin up a real ClickHouse container per run — then publishes coverage to Codecov, to the workflow run summary, and (on `main` only) back into this README. |
| **Pack NuGet** | `main`, `develop` | Packs the library into a `.nupkg`, uploaded as a downloadable workflow artifact. |
| **Publish to NuGet.org** | `main` only | Pushes the package built above to nuget.org. |
| **GitHub Release** | `main` only | Tags the commit and publishes a GitHub Release with the `.nupkg` attached and auto-generated release notes. |

Pushes to `develop` therefore stop after **Pack NuGet** — you get a downloadable prerelease artifact for
every commit without publishing anything permanent to nuget.org or cutting a release. Only `main`
completes the last two jobs.

**Publishing to NuGet.org uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
(OIDC), not a long-lived API key.** The `Publish to NuGet.org` job exchanges a short-lived GitHub OIDC
token for a temporary (1-hour) nuget.org API key at push time via [`NuGet/login`](https://github.com/NuGet/login),
so there is no static secret to leak, rotate, or revoke.

To fork or self-host this pipeline:

1. On [nuget.org](https://www.nuget.org) → your profile → **Trusted Publishing**, add a policy pointing at
   your fork: repository owner, repository name, and workflow file `ci.yml` (filename only, not the
   `.github/workflows/` path).
2. Configure two repository secrets:

| Secret | Used by | Purpose |
|---|---|---|
| `NUGET_USER` | Publish to NuGet.org | Your nuget.org profile name (**not** your email) — passed to `NuGet/login` to identify which trusted publishing policy applies. |
| `CODECOV_TOKEN` | Unit Tests + Integration Tests | Upload token from [codecov.io](https://codecov.io) for this repository (recommended even for public repos, to avoid rate limiting). |

### Per-class coverage detail

Beyond the aggregate percentage shown by the Codecov badge above, the table below is regenerated on
every push to `main` by [ReportGenerator](https://github.com/danielpalme/ReportGenerator) and committed
straight back into this file — see [`generate-readme.ps1`](generate-readme.ps1) and the *Regenerate
README* step in [`ci.yml`](.github/workflows/ci.yml). It reflects coverage as of the last `main` build,
not necessarily the latest commit on `develop`.

**This table is generated — edit [`README.tpl`](README.tpl), not this section, and let CI regenerate it.**

<!-- COVERAGE:START -->
_Coverage table not generated yet. Run `pwsh ./generate-readme.ps1` locally after a coverage run, or push to `main`, to populate this section._
<!-- COVERAGE:END -->

## License

[MIT](LICENSE.md) © Mauro Cardillo
