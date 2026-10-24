# @dataoil/client (JavaScript and TypeScript) sample

The Data Oil client for JavaScript and TypeScript, `@dataoil/client` 0.1.5: nothing but `fetch`, no
dependencies, Node 18 and later.

## Prerequisites

- Node 18 or later.
- `@dataoil/client` 0.1.5 from [npm](https://www.npmjs.com/package/@dataoil/client/v/0.1.5), which
  `package.json` pins and `npm install` fetches. In a project of your own:
  `npm install @dataoil/client@0.1.5`.

```sh
npm install
```

## Run it

```sh
npm run build        # tsc
npm test             # build, and the tests against stubs
npm run dry-run      # the write walk-through, no server
npm start            # the public sandbox, read only
```

With no `DATAOIL_*` variable set, `npm start` signs in to the public sandbox
(`https://sandbox.dataoil.tech`, database `Sandbox`) as its read-only account `suser`. Set
`DATAOIL_URL`, `DATAOIL_DATABASE` and `DATAOIL_TOKEN` (or `DATAOIL_USERNAME` and
`DATAOIL_PASSWORD`), and optionally `DATAOIL_CREATE_DATABASE=true`, to run the write walk-through in
your own tenant instead. For a deployment on its own certificate authority, set
`NODE_EXTRA_CA_CERTS` to its certificate file. If TLS to the sandbox fails with a certificate error,
use a current Node release: it carries its own root certificates, which trust Let's Encrypt.

## What it shows

In the sandbox (`runSandbox`):

| Step | Call |
| --- | --- |
| 1. The databases this account sees | `client.request("GET", "/api/v2/databases")` |
| 2. The collections and their properties | `client.schema()` |
| 3. Read `WorldBank_rows` | `client.statement` with a `:year` parameter and backtick-quoted columns; `for await (... of client.query(...))` for one country's series; an aggregate with `GROUP BY` |
| 4. A passage and its translations | `client.statement` over `WorldBank_chunks` |
| 5. A write, refused | `client.statement("INSERT ...")` throws `DataOilAuthorizationError`: 403 `Security` |

In your own tenant (`run`):

| Step | Call |
| --- | --- |
| 1. Connect, and create the database if asked | `client.request("GET", ".../schema/version")`; `client.request("POST", "/api/v2/databases", { name })` on a `DataOilNotFoundError` |
| 2. A collection with a full-text and a vector index | `client.statement("CREATE ...")` |
| 3. Write | `client.batch([...])` (one transaction) and `client.create(...)` |
| 4. Query | `client.statement` with a parameter; `for await (... of client.query(..., { pageSize: 2 }))` following the cursor |
| 5. Search | `SEARCH_INDEX('Type[body]', :q)` ranked by `$score`; `vector.neighbors('Type[embedding]', :v, 2)` with `VECTOR_DISTANCE` |
| 6. Clean up | `DELETE FROM`, `DROP TYPE`, and the database only when this run created it |

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
    <2zh>  列: 国家, 年份, GDP (美元 ), 人口, 预期寿命, 失业率 ( % ), 二氧化碳排放量 (人均公吨 ), 电力供应 ( % )
5. Write refused as expected: 403 Security - 'suser' may not do that in 'Sandbox'
Done: a read-only walk-through of the sandbox; nothing was written.
```

## Files

- `src/sample.ts` - the settings, both walk-throughs and `main`.
- `src/stub-engine.ts` - the stand-in the dry run and the tests send to: a `fetch` handed to the
  client, answering in the engine's shapes and keeping the notes written. No socket is opened.
- `src/sandbox-stub.ts` - the stand-in for the sandbox in the tests: a few World Bank rows, and
  every write refused.
- `test/sample.test.mjs` - both walk-throughs against the stubs, and the settings.
