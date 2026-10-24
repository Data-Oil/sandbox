# dataoil (Python) sample

The Data Oil client for Python, `dataoil` 0.1.5: standard library only, Python 3.10 and later.

## Prerequisites

- Python 3.10 or later.
- `dataoil` 0.1.5 from [PyPI](https://pypi.org/project/dataoil/0.1.5/), which `requirements.txt`
  pins (`dataoil==0.1.5`). In a project of your own: `python -m pip install dataoil==0.1.5`.

```sh
python -m venv .venv
.venv/bin/python -m pip install -r requirements.txt        # .venv\Scripts\python on Windows
```

`requirements.txt` also installs `certifi`. The client uses Python's own `ssl`, which reads the
operating system's certificate store, and some Windows stores reject the Let's Encrypt chain the
sandbox uses; the sample trusts certifi's bundle instead. If TLS fails with *"certificate has
expired"* or *"unable to get local issuer certificate"*, run `pip install --upgrade certifi`.

On Windows, if Python still reports *"certificate has expired"* for `*.dataoil.tech`, the PC's
intermediate store holds a stale cross-signed copy of "ISRG Root X2" that expired on 2025-09-15:
remove it in `certmgr.msc` under *Intermediate Certification Authorities*, or update Windows' root
certificates. The server's chain is valid.

## Run it

```sh
python -m py_compile sample.py stub_engine.py test_sample.py    # compile check
python -m unittest                                              # the tests against stubs
python sample.py --dry-run                                      # the write walk-through, no server
python sample.py                                                # the public sandbox, read only
```

With no `DATAOIL_*` variable set, `python sample.py` signs in to the public sandbox
(`https://sandbox.dataoil.tech`, database `Sandbox`) as its read-only account `suser`. Set
`DATAOIL_URL`, `DATAOIL_DATABASE` and `DATAOIL_TOKEN` (or `DATAOIL_USERNAME` and
`DATAOIL_PASSWORD`), and optionally `DATAOIL_CREATE_DATABASE=true` and `DATAOIL_CA`, to run the write
walk-through in your own tenant instead.

## What it shows

In the sandbox (`run_sandbox`):

| Step | Call |
| --- | --- |
| 1. The databases this account sees | `client.request("GET", "/api/v2/databases")` |
| 2. The collections and their properties | `client.schema()` |
| 3. Read `WorldBank_rows` | `client.statement` with a `:year` parameter and backtick-quoted columns; `client.query` streaming one country's series; an aggregate with `GROUP BY` |
| 4. A passage and its translations | `client.statement` over `WorldBank_chunks` |
| 5. A write, refused | `client.statement("INSERT ...")` raises `DataOilAuthorizationError`: 403 `Security` |

In your own tenant (`run`):

| Step | Call |
| --- | --- |
| 1. Connect, and create the database if asked | `client.request("GET", ".../schema/version")`; `client.request("POST", "/api/v2/databases", {"name": ...})` on a `DataOilNotFoundError` |
| 2. A collection with a full-text and a vector index | `client.statement("CREATE ...")` |
| 3. Write | `client.batch([...])` (one transaction) and `client.create(...)` |
| 4. Query | `client.statement` with a parameter; `client.query(..., page_size=2)` streaming and following the cursor |
| 5. Search | the client's `Search` helper: `mode="keyword"` over the full-text index, and `mode="vector"` with an `embed` function - here `toy_embed`, standing in for the model the vectors came from |
| 6. Clean up | `DELETE FROM`, `DROP TYPE`, and the database only when this run created it |

`Search` is what the package's LangChain and LlamaIndex retrievers are built on
(`pip install "dataoil[langchain]"`); with a real embedding model, `mode="hybrid"` fuses the two
rankings.

## Expected output

Run against the live sandbox on 2026-10-23, with no `DATAOIL_*` variable set:

```
The public sandbox: https://sandbox.dataoil.tech, database Sandbox, as the read-only account suser. Set DATAOIL_URL and the rest to use your own tenant.
1. Databases: Sandbox
2. Collections: Glossary, WorldBank, WorldBank_rows, WorldBank_chunks, WorldBank_row_vectors, dataoil_catalog
   WorldBank_rows: job_number STRING, correlation_id STRING, row_index LONG, Country STRING, Year LONG, GDP (USD) DOUBLE, Population DOUBLE, Life Expectancy DOUBLE, Unemployment Rate (%) DOUBLE, CO2 Emissions (metric tons per capita) DOUBLE, Access to Electricity (%) DOUBLE
3a. Top 5 GDP in 2015:
    Italy               19,900,750,000,000
    Italy               19,306,190,000,000
    Nigeria             15,033,350,000,000
    India               13,691,740,000,000
    Argentina           10,732,260,000,000
3b. Japan, by year:
    2010  GDP      8,351,720,000,000  life expectancy 83.5
    ...
    2019  GDP     11,529,620,000,000  life expectancy 65.7
3c. Average life expectancy per country (top 5):
    Turkey          70.6  (9 rows)
    Brazil          70.5  (7 rows)
    South Africa    70.4  (8 rows)
    Saudi Arabia    69.5  (13 rows)
    India           69.5  (15 rows)
4. Passage 0: columns: Country, Year, GDP (USD), Population, Life Expectancy, Unemployment Rat
    <2es>  columnas: País, Año, PIB (USD), Población, Esperanza de vida, Tasa de desempleo 
    <2fr>  colonnes: Pays, Année, PIB (USD), Population, Espérance de vie, Taux de chômage 
    <2it>  colonne: Paese, Anno, PIL (USD), Popolazione, Speranza di vita, Tasso di disoccu
    <2ja>  列: 国、年、GDP（米ドル）、人口、平均寿命、失業率（％）、二酸化炭素排出量（人当たりトン）、電気利用率（％）
    <2zh>  列: 国家, 年份, GDP (美元 ), 人口, 预期寿命, 失业率 ( % ), 二氧化碳排放量 (人均公吨 ), 电力供应 ( % )
5. Write refused as expected: 403 Security - 'suser' may not do that in 'Sandbox'
Done: a read-only walk-through of the sandbox; nothing was written.
```

## Files

- `sample.py` - the settings, both walk-throughs and `main`.
- `stub_engine.py` - the stand-ins the dry run and the tests send to: local HTTP servers on
  `127.0.0.1` for as long as the run lasts, answering in the engine's shapes. `StubEngine` keeps
  the notes the write walk-through makes; `SandboxStub` holds a few World Bank rows and refuses
  every write, as the sandbox does. Nothing leaves the machine.
- `test_sample.py` - both walk-throughs against the stubs, and the settings.
