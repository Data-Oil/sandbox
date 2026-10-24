# DataOil.Client sample

The Data Oil client for .NET, `DataOil.Client` 0.2.2: one statement over rows, vectors, keywords and
relationships, streamed as NDJSON, with the tenancy headers, typed errors and retries built in.

## Prerequisites

- The .NET 10 SDK.
- Nothing else: `DataOil.Client` 0.2.2 comes from [nuget.org](https://www.nuget.org/packages/DataOil.Client/0.2.2),
  the only source `nuget.config` here names, and `dotnet test` or `dotnet run` restores it. In a
  project of your own: `dotnet add package DataOil.Client --version 0.2.2`.

## Run it

```sh
dotnet test DataOil.Samples.Client.slnx                           # build, and the tests against stubs
dotnet run --project src/DataOil.Samples.Client -- --dry-run      # the write walk-through, no server
dotnet run --project src/DataOil.Samples.Client                   # the public sandbox, read only
```

With no `DATAOIL_*` variable set, `dotnet run` signs in to the public sandbox
(`https://sandbox.dataoil.tech`, database `Sandbox`) as its read-only account `suser`. Set
`DATAOIL_URL`, `DATAOIL_DATABASE` and `DATAOIL_TOKEN` (or `DATAOIL_USERNAME` and
`DATAOIL_PASSWORD`), and optionally `DATAOIL_CREATE_DATABASE=true` and `DATAOIL_CA`, to run the write
walk-through in your own tenant instead.

## What it shows

In the sandbox (`SandboxWalkthrough`):

| Step | Call |
| --- | --- |
| 1. The databases this account sees | `RequestAsync(HttpMethod.Get, "api/v2/databases")` |
| 2. The collections and their properties | `GetSchemaAsync()`, typed |
| 3. Read `WorldBank_rows` | `StatementAsync` with a `:year` parameter and backtick-quoted columns; `QueryAsync` streaming one country's series; an aggregate with `GROUP BY` |
| 4. A passage and its translations | `StatementAsync` over `WorldBank_chunks` |
| 5. A write, refused | `StatementAsync("INSERT ...")` throws `DataOilAuthorizationException`: 403 `Security` |

In your own tenant (`Walkthrough`):

| Step | Call |
| --- | --- |
| 1. Connect, and create the database if asked | `SchemaVersionAsync`; `RequestAsync(Post, "api/v2/databases", new { name })` on a `DataOilNotFoundException` |
| 2. A collection with a full-text and a vector index | `StatementAsync("CREATE ...")` |
| 3. Write | `BatchAsync` (three inserts, one transaction) and `CreateAsync` (one record, answered with its `rid`) |
| 4. Query | `StatementAsync` with a `:least` parameter; `QueryAsync` streaming two rows a page, following the cursor |
| 5. Search | `SEARCH_INDEX('Type[body]', :q)` ranked by `$score`; `vector.neighbors('Type[embedding]', :v, 2)` with `VECTOR_DISTANCE` |
| 6. Clean up | `DELETE FROM`, `DROP TYPE`, and `DELETE api/v2/databases/{name}` only for a database this run created |

A refusal is a `DataOilException` with `Status`, `ErrorCode`, `Detail` and `CorrelationId`; the
program prints them and exits 2.

## Expected output

Run against the live sandbox on 2026-10-23, with no `DATAOIL_*` variable set:

```
The public sandbox: https://sandbox.dataoil.tech, database Sandbox, as the read-only account suser. Set DATAOIL_URL and the rest to use your own tenant.
1. Databases: Sandbox
2. Collections: Glossary, WorldBank, WorldBank_rows, WorldBank_chunks, WorldBank_row_vectors, dataoil_catalog
   WorldBank_rows: job_number STRING, correlation_id STRING, row_index LONG, Country STRING, Year LONG, GDP (USD) DOUBLE, ...
3a. Top 5 GDP in 2015:
    Italy               19,900,750,000,000
    Italy               19,306,190,000,000
    Nigeria             15,033,350,000,000
    India               13,691,740,000,000
    Argentina           10,732,260,000,000
3b. Japan, by year:
    2010  GDP      8,351,720,000,000  life expectancy 83.5
    ...
3c. Average life expectancy per country (top 5):
    Turkey          70.6  (9 rows)
    ...
4. Passage 0: columns: Country, Year, GDP (USD), Population, Life Expectancy, Unemployment Rat
    <2es>  columnas: País, Año, PIB (USD), Población, Esperanza de vida, Tasa de desempleo 
    ...
    <2ja>  列: 国、年、GDP（米ドル）、人口、平均寿命、失業率（％）、二酸化炭素排出量（人当たりトン）、電気利用率（％）
5. Write refused as expected: 403 Security - 'suser' may not do that in 'Sandbox'
Done: a read-only walk-through of the sandbox; nothing was written.
```

## Files

- `src/DataOil.Samples.Client/SandboxWalkthrough.cs` - the read-only steps for the sandbox.
- `src/DataOil.Samples.Client/Walkthrough.cs` - the write steps for your own tenant.
- `src/DataOil.Samples.Client/SampleSettings.cs` - the environment variables, and the sandbox default.
- `src/DataOil.Samples.Client/StubEngine.cs` - the stand-in the dry run and the tests send to: an
  `HttpMessageHandler` answering in the engine's shapes, keeping what was written. Nothing is sent.
- `src/DataOil.Samples.Client/SandboxStub.cs` - the stand-in for the sandbox in the tests: a few
  World Bank rows, and every write refused.
- `tests/` - both walk-throughs against the stubs, and the settings.
