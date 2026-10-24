# @dataoil/mcp sample

The Data Oil Model Context Protocol server, `@dataoil/mcp` 0.1.5: four tools - `query`, `schema`,
`ask`, `ingest` - over one database, for any MCP host: a desktop assistant, an IDE, an agent
framework. Nothing in the server decides who may see what; the engine does, by the credential the
server is given.

This sample is an MCP client. It starts the server the way a host does - a child process speaking
the protocol on stdio - and calls its tools the way a model would.

## Prerequisites

- Node 18 or later.
- `@dataoil/mcp` 0.1.5 and `@dataoil/client` 0.1.5 from npm
  ([mcp](https://www.npmjs.com/package/@dataoil/mcp/v/0.1.5),
  [client](https://www.npmjs.com/package/@dataoil/client/v/0.1.5)), which `package.json` pins and
  `npm install` fetches; the server depends on the client. The server is also listed in the
  [MCP Registry](https://registry.modelcontextprotocol.io/v0/servers?search=tech.dataoil/mcp) as `tech.dataoil/mcp`. In a project of
  your own: `npm install @dataoil/mcp@0.1.5`, or `npx -y @dataoil/mcp@0.1.5` from a host's settings.

```sh
npm install
```

## Run it

```sh
npm run build        # tsc
npm test             # build, and the tests: the server in-process against stubs
npm run dry-run      # the write walk-through, no server
npm start            # the server on stdio, against the public sandbox, read only
```

With no `DATAOIL_*` variable set, `npm start` starts the server against the public sandbox
(`https://sandbox.dataoil.tech`, database `Sandbox`) as its read-only account `suser`. Set
`DATAOIL_URL`, `DATAOIL_DATABASE` and `DATAOIL_TOKEN` (or `DATAOIL_USERNAME` and
`DATAOIL_PASSWORD`), and optionally `DATAOIL_CREATE_DATABASE=true`, to run the write walk-through in
your own tenant instead. The settings are passed on to the server the sample starts;
`DATAOIL_MAX_ROWS` and `NODE_EXTRA_CA_CERTS` are passed on too.

## What it shows

In the sandbox (`runSandbox`):

| Step | Tool |
| --- | --- |
| The tools a host would show the model | `listTools` |
| The collections and their properties | `schema` with `asTypes: true` |
| The database in prose, as a model reads it | `schema` with no arguments |
| Read `WorldBank_rows` | `query` with a `:year` parameter and backtick-quoted columns |
| A passage and its translations | `query` over `WorldBank_chunks` |
| A write, refused | `ingest`: the tool answers an error carrying the engine's 403 `Security` |

In your own tenant (`run`):

| Step | Tool |
| --- | --- |
| The tools a host would show the model | `listTools` |
| A collection with a full-text and a vector index | `query` - it takes DDL, reads and writes |
| Write three notes | `ingest` with `records` |
| What the database holds | `schema` with `asTypes: true` (without it, the schema in prose) |
| Query and search | `query` with parameters: a filter, `SEARCH_INDEX`, `vector.neighbors` |
| Clean up | `query`: `DELETE FROM`, `DROP TYPE` |

The server has no tool that creates a database, so a database the sample is asked to create is
created - and dropped at the end - with `@dataoil/client`. `ask` is not called: it sends rows to the
tenant's own model endpoint, which a sample cannot assume is configured, and the sandbox has none (it
answers 400 `Configuration` and sends nothing anywhere).

## Expected output

Run against the live sandbox on 2026-10-23, with no `DATAOIL_*` variable set:

```
The public sandbox: https://sandbox.dataoil.tech, database Sandbox, as the read-only account suser. Set DATAOIL_URL and the rest to use your own tenant.
1. tools/list: ask, ingest, query, schema
2. schema {asTypes: true}: 6 collections - Glossary, WorldBank, WorldBank_rows, WorldBank_chunks, WorldBank_row_vectors, dataoil_catalog
3. schema {} (prose): collection V (vertex); collection E (edge); collection Glossary (vertex, extends V); category STRING; checksum ...
4. query: top 3 GDP in 2015 - Italy 19900750000000; Italy 19306190000000; Nigeria 15033350000000
5. query: passage 0 in <2es> <2fr> <2it> <2ja> <2zh> - <2es> columnas: País, Año, PIB (USD), Población, Esperanza de vida ... | <2fr> colonnes: Pays, Année, PIB (USD), Population, Espérance de v ...
6. ingest refused as expected: 403 Security - 'suser' may not write in 'Sandbox'
Done: a read-only walk-through of the sandbox through the server's tools; nothing was written.
```

## In an MCP host

A host starts the server from its configuration. Two shapes most hosts take:

- [`mcp-host.sandbox.json`](mcp-host.sandbox.json) - the public sandbox, read only, ready to use:
  replace `/path/to/samples` with where this folder is (on Windows, with forward slashes, such as
  `C:/src/samples/mcp/node_modules/@dataoil/mcp/dist/cli.js`). The password goes into the JSON as it
  is; `#` and `%` need no escaping there.
- [`mcp-host.example.json`](mcp-host.example.json) - your own tenant: replace the placeholders, and
  give the server an API key with only the rights the assistant should have - `read` alone for one
  that answers questions.

## Files

- `src/sample.ts` - the settings, both walk-throughs and `main`.
- `src/stub-engine.ts` - the stand-in the dry run and the tests send to: a `fetch` handed to the
  server, answering in the engine's shapes. No socket is opened.
- `src/sandbox-stub.ts` - the stand-in for the sandbox in the tests: a few World Bank rows, the
  schema in prose, and every write refused.
- `test/sample.test.mjs` - the tools through the protocol (an in-memory transport pair) against the stubs.
