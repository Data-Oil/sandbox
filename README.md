# Data Oil samples

One small, runnable project per Data Oil package. Run with no settings, each one signs in to the
**public sandbox** as its read-only account and walks through it: the databases, the schema, a few
queries over a World Bank sample table, a passage read in five languages, and a write the sandbox
refuses. Pointed at your own tenant, each one connects, creates a database when you ask it to and
are allowed to, writes a few notes, queries them, searches them by keyword and by meaning, and
drops everything it made. Each has a dry run that needs no server and no credential.

| Folder | Package | Language | Build and test | Dry run |
| --- | --- | --- | --- | --- |
| [`dotnet-client`](dotnet-client/README.md) | `DataOil.Client` 0.2.2 | C#, .NET 10 | `dotnet test DataOil.Samples.Client.slnx` | `dotnet run --project src/DataOil.Samples.Client -- --dry-run` |
| [`efcore`](efcore/README.md) | `DataOil.EntityFrameworkCore` 0.1.4 | C#, .NET 10 | `dotnet test DataOil.Samples.EntityFrameworkCore.slnx` | `dotnet run --project src/DataOil.Samples.EntityFrameworkCore -- --dry-run` |
| [`python`](python/README.md) | `dataoil` 0.1.5 | Python 3.10+ | `python -m unittest` | `python sample.py --dry-run` |
| [`javascript`](javascript/README.md) | `@dataoil/client` 0.1.5 | TypeScript, Node 18+ | `npm test` | `npm run dry-run` |
| [`mcp`](mcp/README.md) | `@dataoil/mcp` 0.1.5 | TypeScript, Node 18+ | `npm test` | `npm run dry-run` |
| [`linqpad`](linqpad/README.md) | `DataOil.LINQPad` 0.1.5 | LINQPad 9 | - | - |

## Before you build

**The packages come from the public registries**, and each sample's own install step fetches the
version it names - there are no package files to copy:

| Package | Registry | Install |
| --- | --- | --- |
| `DataOil.Client` 0.2.2 | [nuget.org](https://www.nuget.org/packages/DataOil.Client/0.2.2) | `dotnet add package DataOil.Client --version 0.2.2` |
| `DataOil.EntityFrameworkCore` 0.1.4 | [nuget.org](https://www.nuget.org/packages/DataOil.EntityFrameworkCore/0.1.4) | `dotnet add package DataOil.EntityFrameworkCore --version 0.1.4` |
| `DataOil.LINQPad` 0.1.5 | [nuget.org](https://www.nuget.org/packages/DataOil.LINQPad/0.1.5) | LINQPad 9: **Add Connection**, **View more drivers...** (see [`linqpad`](linqpad/README.md)) |
| `dataoil` 0.1.5 | [PyPI](https://pypi.org/project/dataoil/0.1.5/) | `python -m pip install dataoil==0.1.5` |
| `@dataoil/client` 0.1.5 | [npm](https://www.npmjs.com/package/@dataoil/client/v/0.1.5) | `npm install @dataoil/client@0.1.5` |
| `@dataoil/mcp` 0.1.5 | [npm](https://www.npmjs.com/package/@dataoil/mcp/v/0.1.5), and the [MCP Registry](https://registry.modelcontextprotocol.io/v0/servers?search=tech.dataoil/mcp) as `tech.dataoil/mcp` | `npm install @dataoil/mcp@0.1.5` |

The .NET samples' `nuget.config` names nuget.org as their only source, the Node samples'
`package-lock.json` resolves from `https://registry.npmjs.org`, and `python/requirements.txt` pins
`dataoil==0.1.5`. So in each folder `dotnet test`, `npm install` or `pip install -r requirements.txt`
is all it takes.

That is all the sandbox needs. For your own tenant you also need a credential: an API key (minted on
the console's API keys page, or with `POST /api/v2/tokens`) or an account name and password. To let
a sample create its own database the credential must be allowed to create databases for your
tenant: a tenant administrator, or a member of the tenant's Database Managers role. Without that
right, point the sample at a database you already have and leave `DATAOIL_CREATE_DATABASE` unset.

## The public sandbox

| | |
| --- | --- |
| Address | `https://sandbox.dataoil.tech` |
| Database | `Sandbox` |
| Account | `suser`, password `hFn3dh#2%hn8A` - public by design, read only |
| Collections | `WorldBank_rows` (`Country`, `Year`, `GDP (USD)`, `Population`, `Life Expectancy`, `Unemployment Rate (%)`, `CO2 Emissions (metric tons per capita)`, `Access to Electricity (%)`, `row_index`), `WorldBank_chunks` (the table's description in passages, with translations into Spanish, French, Italian, Japanese and Chinese), `WorldBank`, `Glossary` |

Every write is refused with 403 `Security`. The sandbox has fair-use limits - about 60 requests a
minute from one address - and a refusal past them is a 429 with `Retry-After`, which the clients
wait out by themselves when it is short. A sample's sandbox walk-through is seven or eight requests.

## Configuration

Every sample reads the same environment variables, and nothing else. **With none of the connection
variables set** (`DATAOIL_URL`, `DATAOIL_DATABASE`, `DATAOIL_TOKEN`, `DATAOIL_USERNAME`,
`DATAOIL_PASSWORD`), a sample uses the public sandbox as above. **Set any of them** and the sandbox
is out of the picture: nothing else is filled in, and every variable that is missing is named.

| Variable | Meaning |
| --- | --- |
| `DATAOIL_URL` | Where the engine answers, such as `https://acme.dataoil.tech` |
| `DATAOIL_DATABASE` | The database the sample works in |
| `DATAOIL_TOKEN` | An API key, `dot_...` - or instead: |
| `DATAOIL_USERNAME`, `DATAOIL_PASSWORD` | An account name and password |
| `DATAOIL_CREATE_DATABASE` | `true` to create the database when it does not exist, and drop it at the end. A database that was already there is never dropped. Ignored in the sandbox |
| `DATAOIL_CA` | (.NET and Python) the certificate file of a deployment on its own certificate authority. Node reads `NODE_EXTRA_CA_CERTS` instead |
| `DATAOIL_JOB_ID` | What the engine's audit and your bill file the work under; each sample has its own default |

```sh
export DATAOIL_URL=https://acme.dataoil.tech
export DATAOIL_DATABASE=sample_notes
export DATAOIL_TOKEN=dot_your_key_here
export DATAOIL_CREATE_DATABASE=true
```

An address on `sandbox.dataoil.tech` always runs the read-only walk-through, whatever else is set.

## What a run does, and costs

**In the sandbox**, a sample lists the databases, reads the schema, runs three queries over
`WorldBank_rows` (the top GDP in 2015, Japan's series by year, average life expectancy per country),
reads the first passage of `WorldBank_chunks` with its translations, and tries one write, which is
refused and reported "as expected". Nothing is written. Each sample's README shows the output of a
run against the live sandbox.

**In your own tenant**, each sample makes a collection named for the run (`SampleNote_` and eight
hex digits; the Entity Framework sample uses `SampleNote`, and stops if the database already has
one), declares four properties, a full-text index and a four-dimension vector index, writes four
notes, reads them back, runs one keyword search and one vector search, and drops the collection -
and the database, when it created it. The vectors are written by hand, four numbers per note for
how much it is about drilling, pipelines, safety and money, so the search can be followed by eye and
needs no model. A run is a few dozen requests and a handful of records, and it is billed to your
tenant like any other work on your data.

## Exit codes

`0` every step ran (in the sandbox: and the write was refused); `1` a setting is missing (each
missing variable is named); `2` the engine refused something it should not have, or the sandbox
accepted a write - the message carries the engine's error code, its sentence and the correlation id
your tenant's activity view is indexed by.

## Licence

The samples and the packages are covered by the Data Oil Software Licence, in [`LICENSE`](LICENSE).
In short:

- **The sample code** - everything in this folder - is sample code under
  clause 6: it may be copied and adapted to build your own applications that reach Data Oil for a
  legitimate use.
- **The packages** (`DataOil.Client`, `DataOil.EntityFrameworkCore`, `DataOil.LINQPad`, `dataoil`,
  `@dataoil/client`, `@dataoil/mcp`) may be installed and used only to reach Data Oil. They may not
  be copied beyond that, modified, redistributed or reverse engineered.
- **Attacks on the platform are prohibited**: hacking, probing or scanning it, getting round its
  authentication, rate limits or read-only restrictions, flooding it or exhausting its resources,
  and any unauthorised access - the public sandbox included. Security testing needs Data Oil's prior
  written authorisation; report a vulnerability privately to info@dataoil.tech.

Data Oil itself is a commercial service; using it beyond the public sandbox needs a credential
issued under the Data Oil customer agreement. The licence text governs where this summary is short.

**What the sandbox does and doesn't do:** SQL, schema reads and keyword search across
five languages work here. Semantic (vector) search and cited answers are switched off in
the free sandbox, because they need GPU; they run on your own database.

**Status:** the engine runs our own products in production. The public clients are new,
the source is closed, and the team is small. Building something real? Apply for one of
50 founding builder seats at https://sandbox.dataoil.tech
Below that, show the hybrid query labelled "on your own database", next to a keyword query that runs in the sandbox as-is.
