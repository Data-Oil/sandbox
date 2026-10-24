# DataOil.EntityFrameworkCore sample

The Entity Framework Core 10 provider for Data Oil, `DataOil.EntityFrameworkCore` 0.1.4: an ordinary
strongly typed `DbContext`, checked against the database's schema at run time, LINQ translated into
the engine's dialect, and the record's version as the concurrency token.

## Prerequisites

- The .NET 10 SDK.
- Nothing else: `DataOil.EntityFrameworkCore` 0.1.4 comes from
  [nuget.org](https://www.nuget.org/packages/DataOil.EntityFrameworkCore/0.1.4), the only source
  `nuget.config` here names, and `dotnet test` or `dotnet run` restores it. The provider brings
  `DataOil.Client` 0.2.2 and EF Core 10.0.13. In a project of your own:
  `dotnet add package DataOil.EntityFrameworkCore --version 0.1.4`.

## Run it

```sh
dotnet test DataOil.Samples.EntityFrameworkCore.slnx                          # build, and the offline tests
dotnet run --project src/DataOil.Samples.EntityFrameworkCore -- --dry-run     # the models and their statements, no server
dotnet run --project src/DataOil.Samples.EntityFrameworkCore                  # the public sandbox, read only
```

With no `DATAOIL_*` variable set, `dotnet run` reads the public sandbox
(`https://sandbox.dataoil.tech`, database `Sandbox`) as its read-only account `suser`. Set the same
variables as every sample - `DATAOIL_URL`, `DATAOIL_DATABASE`, `DATAOIL_TOKEN` (or
`DATAOIL_USERNAME` and `DATAOIL_PASSWORD`), and optionally `DATAOIL_CREATE_DATABASE=true` and
`DATAOIL_CA` - to run the write walk-through in your own tenant instead.

The dry run builds both contexts over a connection that is never opened and prints the models - the
note keyed by `@rid`, versioned by `@version`; the World Bank row with its bracketed column names -
and what their LINQ queries become in the engine's dialect.

## What it shows

In the sandbox (`SandboxWalkthrough`, over `WorldBankContext`):

| Step | How |
| --- | --- |
| 1-2. Databases and collections | `DataOilClient`, which the provider brings: `RequestAsync(Get, "api/v2/databases")` and `GetSchemaAsync()` |
| 3. LINQ over `WorldBankRow` | `Where`/`OrderByDescending`/`Take` with a captured parameter, a series `OrderBy`, and `GroupBy` with `Average` and `Count`; the columns with spaces (`[Column("GDP (USD)")]`) are quoted with backticks |
| 4. A passage and its translations | `context.DataOil().StatementAsync(...)`, the engine's own statement, for a list of maps LINQ cannot say |
| 5. `SaveChanges`, refused | an update guarded by `@version`, answered 403 `Security`: the account is read only |

In your own tenant (`Walkthrough`, over `NotesContext`). The provider maps classes onto a collection
that exists; it does not make databases or collections. So the database and the `SampleNote`
collection are made and dropped with `DataOil.Client`, and everything in between goes through the
context:

| Step | How |
| --- | --- |
| 3. Write | `AddRange` and `SaveChangesAsync`; each entity comes back with the `@rid` the engine gave it |
| | The vectors, which the model does not map: `context.DataOil().StatementAsync("UPDATE #12:0 SET embedding = :v")` over the context's own connection |
| | `ChangeTracker.Clear()` afterwards: those updates moved each record's version, so the tracked copies are stale and saving one would be refused as a concurrency failure |
| 4. Query and update | LINQ `Where`/`OrderByDescending`/`Select`; `SingleAsync`, change a property, `SaveChangesAsync` - guarded by `@version` |
| 5. Search | `SEARCH_INDEX` and `vector.neighbors` through `context.DataOil()`, which LINQ cannot say |
| 6. Delete | `RemoveRange` and `SaveChangesAsync`, each delete guarded by the version it read |

`SampleNote` is a fixed name because a context's model is built once per type. If the database
already has a `SampleNote`, the sample stops before touching it.

## Expected output

Run against the live sandbox on 2026-10-23, with no `DATAOIL_*` variable set:

```
The public sandbox: https://sandbox.dataoil.tech, database Sandbox, as the read-only account suser. Set DATAOIL_URL and the rest to use your own tenant.
1. Databases: Sandbox
2. Collections: Glossary, WorldBank, WorldBank_rows, WorldBank_chunks, WorldBank_row_vectors, dataoil_catalog
3a. Top 5 GDP in 2015:
    Italy               19,900,750,000,000
    Italy               19,306,190,000,000
    Nigeria             15,033,350,000,000
    India               13,691,740,000,000
    Argentina           10,732,260,000,000
    LINQ became: -- p='2015' -- p1='5' -- dataoil:project __0,__1 SELECT `w`.`Country` AS `__0`, `w`.`GDP (USD)` AS `__1` FROM `WorldBank_rows` AS `w` WHERE `w`.`Year` = :p ORDER BY `w`.`GDP (USD)` DESC LIMIT :p1
3b. Japan, by year:
    2010  GDP      8,351,720,000,000  life expectancy 83.5
    ...
3c. Average life expectancy per country (top 5):
    Turkey          70.6  (9 rows)
    ...
4. Passage 0: columns: Country, Year, GDP (USD), Population, Life Expectancy, Unemployment Rat
    <2es>  columnas: País, Año, PIB (USD), Población, Esperanza de vida, Tasa de desempleo 
    ...
5. SaveChanges refused as expected: 403 Security - 'suser' may not do that in 'Sandbox'
Done: a read-only walk-through of the sandbox through the context; nothing was written.
```

## Files

- `src/DataOil.Samples.EntityFrameworkCore/WorldBankContext.cs` - the sandbox's row and its context.
- `src/DataOil.Samples.EntityFrameworkCore/SandboxWalkthrough.cs` - the read-only steps for the sandbox.
- `src/DataOil.Samples.EntityFrameworkCore/NotesContext.cs` - the note and its context.
- `src/DataOil.Samples.EntityFrameworkCore/Walkthrough.cs` - the write steps, and the dry run's translations.
- `src/DataOil.Samples.EntityFrameworkCore/SampleSettings.cs` - the environment variables, and the sandbox default.
- `tests/` - both models, their translations and the settings, offline.
