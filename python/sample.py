"""The Data Oil Python client sample.

    python sample.py              the public sandbox, read only - or your own tenant, when the
                                  DATAOIL_* variables name it
    python sample.py --dry-run    against a stub in this process, with no server at all

With no DATAOIL_* variables set, the sample signs in to the public sandbox
(https://sandbox.dataoil.tech, database Sandbox) as its read-only account, lists the databases,
reads the schema, queries the World Bank sample table, reads one passage's translations, and shows
that a write is refused.

With DATAOIL_URL and the rest set, it works in your own tenant: connect, create a database (when
asked to and allowed to), write notes, query them, search them by keyword and by meaning, and leave
nothing behind. See README.md for the variables.
"""

from __future__ import annotations

import os
import sys
import uuid
from dataclasses import dataclass, field
from typing import Callable, Mapping, Sequence, TextIO
from urllib.parse import urlparse

from dataoil import (
    BatchOperation, DataOilAuthorizationError, DataOilClient, DataOilError, DataOilNotFoundError, Search,
)

# The public sandbox. Its account is public by design: it can read the database 'Sandbox' and
# nothing else, and every write is refused. The password holds '#' and '%'; as a Python string it
# needs no escaping, because the client puts it into the Basic auth header itself.
SANDBOX_URL = "https://sandbox.dataoil.tech"
SANDBOX_DATABASE = "Sandbox"
SANDBOX_USERNAME = "suser"
SANDBOX_PASSWORD = "hFn3dh#2%hn8A"

# The variables that name a connection. Set none of them and the sample uses the sandbox.
CONNECTION_VARIABLES = ("DATAOIL_URL", "DATAOIL_DATABASE", "DATAOIL_TOKEN", "DATAOIL_USERNAME", "DATAOIL_PASSWORD")


def is_sandbox(url: str) -> bool:
    """Whether an address is the public sandbox, where the read-only walk-through is the one that runs."""
    return (urlparse(url).hostname or "").lower() == urlparse(SANDBOX_URL).hostname

# Four numbers per note - how much it is about drilling, pipelines, safety and money - written by
# hand so the vector search can be followed by eye and needs no model. A real application stores
# what its embedding model produced, and searches with the same model's vector for the question.
NOTES = [
    ("Casing failure on well 7", "The casing failed at 2300 metres during cementing, and the crew stopped drilling.", 5, [0.9, 0.1, 0.4, 0.0]),
    ("Pipeline pigging schedule", "Pigging the export pipeline every second week keeps wax out of the line.", 3, [0.0, 0.9, 0.1, 0.1]),
    ("Permit to work audit", "The audit found two permits signed after the work had started.", 4, [0.1, 0.1, 0.9, 0.1]),
]

TOPICS = (
    ("drill", "well", "casing", "cement"),
    ("pipeline", "pigging", "wax", "export"),
    ("safety", "permit", "audit", "incident"),
    ("rate", "cost", "price", "budget"),
)


def toy_embed(text: str) -> list[float]:
    """Stands in for an embedding model: how often the question names each of the four topics."""
    words = text.lower()
    return [float(sum(words.count(word) for word in topic)) for topic in TOPICS]


@dataclass
class Settings:
    """Where the sample connects and as whom.

    The only default that reaches a server is the public sandbox, as its read-only account. Name
    any connection variable and the sandbox is out of the picture: every one that is missing is
    reported, rather than filled in.
    """

    url: str
    database: str
    token: str | None = None
    username: str | None = None
    password: str | None = None
    ca_file: str | None = None
    create_database: bool = False
    job_id: str = "sample-python-client"
    sandbox: bool = False

    @staticmethod
    def for_sandbox(ca_file: str | None = None, job_id: str | None = None) -> "Settings":
        return Settings(
            url=SANDBOX_URL, database=SANDBOX_DATABASE, username=SANDBOX_USERNAME, password=SANDBOX_PASSWORD,
            ca_file=ca_file, job_id=job_id or "sample-python-client", sandbox=True,
        )

    @staticmethod
    def from_environment(env: Mapping[str, str]) -> tuple["Settings | None", list[str]]:
        def value(name: str) -> str | None:
            held = (env.get(name) or "").strip()
            return held or None

        if all(value(name) is None for name in CONNECTION_VARIABLES):
            return Settings.for_sandbox(value("DATAOIL_CA"), value("DATAOIL_JOB_ID")), []

        problems = []
        url, database = value("DATAOIL_URL"), value("DATAOIL_DATABASE")
        token, username, password = value("DATAOIL_TOKEN"), value("DATAOIL_USERNAME"), value("DATAOIL_PASSWORD")
        if url is None:
            problems.append("DATAOIL_URL is not set: the address the engine answers on, such as https://acme.dataoil.tech.")
        if database is None:
            problems.append("DATAOIL_DATABASE is not set: the database the sample works in.")
        if (token is None) == (username is None):
            problems.append("Set DATAOIL_TOKEN, or DATAOIL_USERNAME and DATAOIL_PASSWORD, and not both.")
        elif username is not None and password is None:
            problems.append("DATAOIL_PASSWORD is not set for DATAOIL_USERNAME.")
        if problems:
            return None, problems
        return Settings(
            url=url, database=database, token=token, username=username, password=password,
            ca_file=value("DATAOIL_CA"),
            create_database=(value("DATAOIL_CREATE_DATABASE") or "").lower() == "true",
            job_id=value("DATAOIL_JOB_ID") or "sample-python-client",
            sandbox=is_sandbox(url),
        ), []


@dataclass
class SandboxResult:
    databases: list[str]
    collections: list[str]
    top_gdp: list[str]
    japan_rows: int
    longest_lived: list[str]
    languages: list[str]
    write_refused: str | None = None
    columns: list[str] = field(default_factory=list)


def run_sandbox(client: DataOilClient, out: TextIO) -> SandboxResult:
    """The read-only walk-through: what the sandbox account can do, and the write it cannot."""

    # 1. The databases this account can see.
    databases = list(client.request("GET", "/api/v2/databases")["databases"])
    print(f"1. Databases: {', '.join(databases)}", file=out)

    # 2. The schema: the collections (types) this account may read, with their properties.
    types = [t for t in client.schema()["types"] if t["name"] not in ("V", "E")]
    collections = [t["name"] for t in types]
    rows_type = next(t for t in types if t["name"] == "WorldBank_rows")
    columns = [p["name"] for p in rows_type["properties"]]
    print(f"2. Collections: {', '.join(collections)}", file=out)
    print(f"   WorldBank_rows: {', '.join(p['name'] + ' ' + p['type'] for p in rows_type['properties'])}", file=out)

    # 3a. Top GDP in one year. Column names with spaces or brackets are quoted with backticks;
    #     values go in as :name parameters, never pasted into the statement.
    print("3a. Top 5 GDP in 2015:", file=out)
    top = client.statement(
        "SELECT Country, `GDP (USD)` AS gdp FROM WorldBank_rows WHERE Year = :year ORDER BY `GDP (USD)` DESC LIMIT 5",
        {"year": 2015})
    for row in top:
        print(f"    {row['Country']:<15} {row['gdp']:>22,.0f}", file=out)

    # 3b. One country's series, streamed: query() follows the cursor across pages.
    print("3b. Japan, by year:", file=out)
    japan = 0
    for row in client.query(
            "SELECT Year, `GDP (USD)` AS gdp, `Life Expectancy` AS life FROM WorldBank_rows "
            "WHERE Country = :country ORDER BY Year", {"country": "Japan"}):
        japan += 1
        print(f"    {row['Year']}  GDP {row['gdp']:>22,.0f}  life expectancy {row['life']}", file=out)

    # 3c. An aggregate: average life expectancy per country.
    print("3c. Average life expectancy per country (top 5):", file=out)
    lived = client.statement(
        "SELECT Country, avg(`Life Expectancy`) AS life, count(*) AS n FROM WorldBank_rows "
        "GROUP BY Country ORDER BY life DESC LIMIT 5")
    for row in lived:
        print(f"    {row['Country']:<15} {row['life']:.1f}  ({row['n']} rows)", file=out)

    # 4. One passage of the table's description, with the translations made when it was loaded.
    #    Its second line lists the column names; show it in each language.
    passage = client.statement(
        "SELECT chunk_index, content, translations FROM WorldBank_chunks ORDER BY chunk_index LIMIT 1")[0]
    print(f"4. Passage {passage['chunk_index']}: {_second_line(passage['content'])}", file=out)
    languages = []
    for translation in passage.get("translations") or []:
        languages.append(translation["code"])  # the language tag: <2es>, <2fr>, <2it>, <2ja>, <2zh>
        print(f"    {translation['code']}  {_second_line(translation['content'])}", file=out)

    result = SandboxResult(databases, collections, [r["Country"] for r in top], japan,
                           [r["Country"] for r in lived], languages, columns=columns)

    # 5. A write is refused: the account is read only. The refusal is a typed exception carrying
    #    the engine's status, stable error code and correlation id.
    try:
        client.statement("INSERT INTO WorldBank_rows SET Country = 'Atlantis', Year = 2020")
        print("5. The write was accepted - that should not happen on the sandbox.", file=out)
    except DataOilAuthorizationError as refused:
        result.write_refused = f"{refused.status} {refused.error_code}"
        print(f"5. Write refused as expected: {refused.status} {refused.error_code} - {refused.detail}", file=out)
    return result


def _second_line(text: str) -> str:
    lines = text.splitlines()
    return (lines[1] if len(lines) > 1 else lines[0] if lines else "")[:80]


@dataclass
class Result:
    created_database: bool
    written: int
    highly_rated: list[str]
    streamed: int
    keyword_hit: str | None
    nearest_hit: str | None


def run(client: DataOilClient, create_database: bool, out: TextIO, type_name: str | None = None) -> Result:
    """Every step, against the database the client names."""
    type_name = type_name or "SampleNote_" + uuid.uuid4().hex[:8]

    # 1. Connect. The schema version is the smallest answer that proves the address, the credential
    #    and the database; a refusal is a typed exception carrying the engine's own code.
    created = False
    try:
        version = client.request("GET", f"/api/v2/databases/{client.database}/schema/version")["version"]
        print(f"1. Connected to {client.database}: schema version {version}.", file=out)
    except DataOilNotFoundError:
        if not create_database:
            raise
        # Creating a database needs the right to: a tenant administrator, or a role holding the
        # tenant's database capability. Anybody else is refused with a 403 that says so.
        client.request("POST", "/api/v2/databases", {"name": client.database})
        created = True
        print(f"1. Created the database {client.database}.", file=out)

    try:
        try:
            # 2. A collection with declared properties, a keyword index and a vector index.
            for statement in (
                f"CREATE DOCUMENT TYPE {type_name}",
                f"CREATE PROPERTY {type_name}.title STRING",
                f"CREATE PROPERTY {type_name}.body STRING",
                f"CREATE PROPERTY {type_name}.rating INTEGER",
                f"CREATE PROPERTY {type_name}.embedding ARRAY_OF_FLOATS",
                f"CREATE INDEX ON {type_name} (body) FULL_TEXT",
                f"CREATE INDEX ON {type_name} (embedding) LSM_VECTOR METADATA {{dimensions: 4, similarity: 'COSINE'}}",
            ):
                client.statement(statement)
            print(f"2. Created the collection {type_name}, with a full-text index on body and a vector index on embedding.", file=out)

            # 3. Write: three notes in one batch (one transaction: all or none), and one more through
            #    the documents route, which answers the record with its identity.
            client.batch([
                BatchOperation(
                    f"INSERT INTO {type_name} SET title = :title, body = :body, rating = :rating, embedding = :embedding",
                    {"title": title, "body": body, "rating": rating, "embedding": vector},
                )
                for title, body, rating, vector in NOTES
            ])
            record = client.create(type_name, {
                "title": "Rig day rate", "body": "The day rate for the rig rises in March.", "rating": 2,
                "embedding": [0.3, 0.0, 0.0, 0.9],
            })
            written = len(NOTES) + 1
            print(f"3. Wrote {len(NOTES)} notes in one batch, and one more as record {record['rid']}.", file=out)

            # 4. Query: one page into a list, then the whole collection streamed two rows a page,
            #    following the cursor each page's trailer carries.
            highly_rated = [row["title"] for row in client.statement(
                f"SELECT title, rating FROM {type_name} WHERE rating >= :least ORDER BY rating DESC", {"least": 4})]
            streamed = sum(1 for _ in client.query(f"SELECT title FROM {type_name} ORDER BY title", page_size=2))
            print(f"4. Rated 4 or more: {', '.join(highly_rated)}. Streamed {streamed} rows two at a time.", file=out)

            # 5. Search, through the client's retrieval helper: by keyword (the full-text index,
            #    ranked by BM25) and by meaning (the vector index, with the question embedded by the
            #    same function that stands in for the model the vectors came from).
            by_keyword = Search(client, type_name, text="body", fields=("title",), mode="keyword").run("casing cementing", k=3)
            by_meaning = Search(client, type_name, text="body", fields=("title",), embed=toy_embed, mode="vector").run(
                "what went wrong with the casing while drilling the well?", k=2)
            keyword_hit = by_keyword[0].record.get("title") if by_keyword else None
            nearest_hit = by_meaning[0].record.get("title") if by_meaning else None
            print(f"5. Keyword search for 'casing cementing' ranked first: {keyword_hit or '(nothing)'}. "
                  f"Nearest by meaning: {nearest_hit or '(nothing)'}.", file=out)

            return Result(created, written, highly_rated, streamed, keyword_hit, nearest_hit)
        finally:
            # 6. Clean up the collection, even when a step failed.
            _clean_up(out, f"the collection {type_name}", lambda: (
                client.statement(f"DELETE FROM {type_name}"), client.statement(f"DROP TYPE {type_name}")))
    finally:
        # ...and the database, but only when this run made it: one that was there before is somebody's.
        if created:
            _clean_up(out, f"the database {client.database}, which this run created",
                      lambda: client.request("DELETE", f"/api/v2/databases/{client.database}"))


def _clean_up(out: TextIO, what: str, step: Callable[[], object]) -> None:
    """One clean-up step, reported rather than raised, so it cannot hide the failure that led here."""
    try:
        step()
        print(f"6. Dropped {what}.", file=out)
    except DataOilError as failure:
        print(f"6. Could not drop {what}: {failure.error_code} {failure.detail}", file=out)


def main(argv: Sequence[str], env: Mapping[str, str] = os.environ, out: TextIO = sys.stdout, err: TextIO = sys.stderr) -> int:
    """0 when every step ran; 1 for missing settings; 2 when the engine refused something."""
    if "--dry-run" in argv:
        from stub_engine import StubEngine

        print("Dry run: the answers come from a stub in this process, not from an engine. Nothing leaves this machine.", file=out)
        with StubEngine() as stub:
            settings = Settings(url=stub.url, database="sample_notes", token="dot_placeholder", create_database=True)
            return _run(settings, out, err)

    settings, problems = Settings.from_environment(env)
    if settings is None:
        for problem in problems:
            print(problem, file=err)
        print("See README.md, or unset every DATAOIL_* variable to use the public sandbox, "
              "or run with --dry-run to see the sample work without a server.", file=err)
        return 1
    # The sandbox's translations include Japanese and Chinese; make sure the console can show them.
    if hasattr(out, "reconfigure"):
        out.reconfigure(encoding="utf-8")
    return _run(settings, out, err)


def trusted_roots(ca_file: str | None) -> str | None:
    """The certificates to trust: DATAOIL_CA when set, else certifi's current bundle when installed.

    The client uses Python's own ``ssl``, which reads the operating system's store; some Windows
    machines' stores reject the Let's Encrypt chain the sandbox and Data Oil's hosted tenants use.
    certifi carries the current roots, and requirements.txt installs it.
    """
    if ca_file:
        return ca_file
    try:
        import certifi
    except ImportError:
        return None
    return certifi.where()


def _run(settings: Settings, out: TextIO, err: TextIO) -> int:
    client = DataOilClient(
        settings.url, settings.database, token=settings.token, username=settings.username,
        password=settings.password, job_id=settings.job_id,
        ca_file=trusted_roots(settings.ca_file) if settings.url.startswith("https:") else settings.ca_file,
    )
    if settings.sandbox:
        print(f"The public sandbox: {settings.url}, database {settings.database}, as the read-only account "
              f"{settings.username}. Set DATAOIL_URL and the rest to use your own tenant.", file=out)
        try:
            sandboxed = run_sandbox(client, out)
        except DataOilError as refused:
            print(f"The engine refused: {refused.status} {refused.error_code}: {refused.detail} "
                  f"(correlation {refused.correlation_id})", file=err)
            return 2
        if sandboxed.write_refused is None:
            return 2
        print("Done: a read-only walk-through of the sandbox; nothing was written.", file=out)
        return 0
    try:
        result = run(client, settings.create_database, out)
    except DataOilError as refused:
        # Every refusal carries a stable code, the engine's own sentence and a correlation id that
        # the engine's logs and the tenant's activity view are indexed by.
        print(f"The engine refused: {refused.status} {refused.error_code}: {refused.detail} "
              f"(correlation {refused.correlation_id})", file=err)
        return 2
    print(f"Done: {result.written} notes written, searched and cleaned up.", file=out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
