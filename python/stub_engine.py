"""A stand-in for the engine, for the dry run and the tests.

It listens on 127.0.0.1 on a port the system picks, for as long as the ``with`` block lasts, and
answers the requests this sample sends in the shapes the engine answers them: NDJSON rows with a
``{"returned", "more", "cursor"}`` trailer, a 404 problem document for a database that does not
exist, 201 for a created database or record.

It is not an engine. It keeps the notes the sample writes and works the sample's own questions out
from them - a rating filter, a word match for the keyword search, cosine distance for the vector
search - so a dry run prints answers that follow from what was written. A statement it does not
recognise is a 400, so a sample that drifts from what the stub knows fails its test rather than
passing on a guess.
"""

from __future__ import annotations

import json
import math
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any


class StubEngine:
    def __init__(self, database_exists: bool = False) -> None:
        self.database_exists = database_exists
        self.notes: list[dict[str, Any]] = []
        self.requests: list[str] = []
        self.every_request_carried_the_headers = True
        self._schema_version = 0
        self._server = ThreadingHTTPServer(("127.0.0.1", 0), self._handler())
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self._server.server_address[1]}"

    def __enter__(self) -> "StubEngine":
        self._thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self._server.shutdown()
        self._server.server_close()

    # ------------------------------------------------------------------ the answers

    def answer(self, method: str, path: str, headers: Any, body: Any) -> tuple[int, str, str]:
        self.requests.append(f"{method} {path}")
        self.every_request_carried_the_headers &= bool(
            headers.get("X-Job-ID") and headers.get("X-Database") and headers.get("Authorization"))

        if method == "POST" and path == "/api/v2/databases":
            self.database_exists = True
            return 201, "application/json", json.dumps({"name": body["name"]})
        if not self.database_exists:
            return _problem(404, "DatabaseNotFound", "There is no database by that name that this caller may see.")
        if method == "DELETE" and path.startswith("/api/v2/databases/") and path.count("/") == 4:
            self.database_exists = False
            return 204, "application/json", ""
        if method == "GET" and path.endswith("/schema/version"):
            return 200, "application/json", json.dumps({"version": self._schema_version})
        if method == "POST" and path.endswith("/batch"):
            return 200, "application/json", json.dumps({"results": [self._insert(o["params"]) for o in body["operations"]]})
        if method == "POST" and path.endswith("/documents"):
            rid = self._insert(body["properties"])
            return 201, "application/json", json.dumps({"rid": rid, "type": body["type"], "properties": body["properties"]})
        if method == "POST" and path.endswith("/query"):
            return self._statement(body)
        return _problem(404, "RouteNotFound", f"The stub does not answer {method} {path}.")

    def _statement(self, body: dict[str, Any]) -> tuple[int, str, str]:
        sql: str = body.get("query") or body.get("command") or ""
        params = body.get("params") or {}

        if sql.startswith(("CREATE ", "DROP ")):
            self._schema_version += 1
            return _rows([{"changed": True}])
        if sql.startswith("DELETE FROM "):
            count, self.notes = len(self.notes), []
            return _rows([{"count": count}])
        if "SEARCH_INDEX(" in sql:
            words = params["q"].lower().split()
            scored = [(sum(w in n["body"].lower() for w in words), n) for n in self.notes]
            ranked = sorted((s for s in scored if s[0] > 0), key=lambda s: -s[0])[: params.get("k", 3)]
            return _rows([{"@rid": n["@rid"], "body": n["body"], "title": n["title"], "score": float(s)} for s, n in ranked])
        if "vector.neighbors(" in sql:
            question = params["v"]
            ranked = sorted(((1 - _cosine(question, n["embedding"]), n) for n in self.notes), key=lambda s: s[0])[: params.get("k", 2)]
            return _rows([{"@rid": n["@rid"], "body": n["body"], "title": n["title"], "distance": round(d, 6)} for d, n in ranked])
        if "WHERE rating >= :least" in sql:
            chosen = sorted((n for n in self.notes if n["rating"] >= params["least"]), key=lambda n: -n["rating"])
            return _rows([{"title": n["title"], "rating": n["rating"]} for n in chosen])
        if "ORDER BY title" in sql:
            # Paged as the engine pages: pageSize rows, and a cursor while rows remain.
            size = body.get("pageSize") or 500
            start = int((body.get("cursor") or "stub-0").split("-")[1])
            ordered = sorted(self.notes, key=lambda n: n["title"])
            page = ordered[start:start + size]
            more = start + len(page) < len(ordered)
            return _rows([{"title": n["title"]} for n in page], more, f"stub-{start + len(page)}" if more else None)
        return _problem(400, "StatementNotRecognised", f"The stub does not know the statement: {sql}")

    def _insert(self, properties: dict[str, Any]) -> str:
        rid = f"#12:{len(self.notes)}"
        self.notes.append({**properties, "@rid": rid})
        return rid

    def _handler(self) -> type[BaseHTTPRequestHandler]:
        stub = self

        class Handler(BaseHTTPRequestHandler):
            def _serve(self) -> None:
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                status, kind, text = stub.answer(self.command, self.path, self.headers, json.loads(raw) if raw else None)
                payload = text.encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", kind)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_GET = do_POST = do_DELETE = _serve

            def log_message(self, *_: object) -> None:  # quiet: the sample's own output is the point
                pass

        return Handler


class SandboxStub(StubEngine):
    """A stand-in for the public sandbox: a few World Bank rows, one passage with its translations,
    and a read-only account - every write is refused with 403 ``Security``, as the sandbox refuses it.

    Like ``StubEngine`` it recognises only the statements the sample sends; anything else is a 400.
    """

    ROWS = [
        {"row_index": 0, "Country": "Italy", "Year": 2015, "GDP (USD)": 19900750000000.0, "Life Expectancy": 71.2},
        {"row_index": 1, "Country": "Nigeria", "Year": 2015, "GDP (USD)": 15033350000000.0, "Life Expectancy": 55.0},
        {"row_index": 2, "Country": "India", "Year": 2015, "GDP (USD)": 13691740000000.0, "Life Expectancy": 69.5},
        {"row_index": 3, "Country": "Japan", "Year": 2010, "GDP (USD)": 8351720000000.0, "Life Expectancy": 83.5},
        {"row_index": 4, "Country": "Japan", "Year": 2015, "GDP (USD)": 8922760000000.0, "Life Expectancy": 68.7},
        {"row_index": 5, "Country": "Turkey", "Year": 2012, "GDP (USD)": 5120000000000.0, "Life Expectancy": 70.6},
    ]
    COLUMNS = ["row_index LONG", "Country STRING", "Year LONG", "GDP (USD) DOUBLE", "Population DOUBLE",
               "Life Expectancy DOUBLE", "Unemployment Rate (%) DOUBLE",
               "CO2 Emissions (metric tons per capita) DOUBLE", "Access to Electricity (%) DOUBLE"]
    PASSAGE = {
        "chunk_index": 0,
        "content": "World Bank indicators\ncolumns: Country, Year, GDP (USD), Population, Life Expectancy",
        "translations": [
            {"code": "<2es>", "content": "Indicadores del Banco Mundial\ncolumnas: País, Año, PIB (USD), Población"},
            {"code": "<2fr>", "content": "Indicateurs de la Banque mondiale\ncolonnes: Pays, Année, PIB (USD), Population"},
        ],
    }

    def __init__(self) -> None:
        super().__init__(database_exists=True)

    def answer(self, method: str, path: str, headers: Any, body: Any) -> tuple[int, str, str]:
        self.requests.append(f"{method} {path}")
        self.every_request_carried_the_headers &= bool(
            headers.get("X-Job-ID") and headers.get("X-Database") and headers.get("Authorization"))

        if method == "GET" and path == "/api/v2/databases":
            return 200, "application/json", json.dumps({"databases": ["Sandbox"]})
        if method == "GET" and path.endswith("/schema"):
            types = [{"name": n, "properties": [], "description": None} for n in ("V", "E", "Glossary", "WorldBank", "WorldBank_chunks")]
            types.insert(4, {"name": "WorldBank_rows", "description": None, "properties": [
                {"name": c.rsplit(" ", 1)[0], "type": c.rsplit(" ", 1)[1]} for c in self.COLUMNS]})
            return 200, "application/json", json.dumps({"types": types})
        if method == "POST" and path.endswith("/query"):
            return self._read(body)
        return _problem(403, "Security", "'suser' may not do that in 'Sandbox'")

    def _read(self, body: dict[str, Any]) -> tuple[int, str, str]:
        sql: str = body.get("query") or body.get("command") or ""
        params = body.get("params") or {}
        if not sql.lstrip().upper().startswith("SELECT"):
            return _problem(403, "Security", "'suser' may not do that in 'Sandbox'")
        if "WHERE Year = :year" in sql:
            chosen = sorted((r for r in self.ROWS if r["Year"] == params["year"]), key=lambda r: -r["GDP (USD)"])[:5]
            return _rows([{"Country": r["Country"], "gdp": r["GDP (USD)"]} for r in chosen])
        if "WHERE Country = :country" in sql:
            chosen = sorted((r for r in self.ROWS if r["Country"] == params["country"]), key=lambda r: r["Year"])
            return _rows([{"Year": r["Year"], "gdp": r["GDP (USD)"], "life": r["Life Expectancy"]} for r in chosen])
        if "GROUP BY Country" in sql:
            countries: dict[str, list[float]] = {}
            for r in self.ROWS:
                countries.setdefault(r["Country"], []).append(r["Life Expectancy"])
            ranked = sorted(((c, sum(v) / len(v), len(v)) for c, v in countries.items()), key=lambda t: -t[1])[:5]
            return _rows([{"Country": c, "life": life, "n": n} for c, life, n in ranked])
        if "FROM WorldBank_chunks" in sql:
            return _rows([self.PASSAGE])
        return _problem(400, "StatementNotRecognised", f"The stub does not know the statement: {sql}")


def _rows(rows: list[dict[str, Any]], more: bool = False, cursor: str | None = None) -> tuple[int, str, str]:
    trailer: dict[str, Any] = {"returned": len(rows), "more": more}
    if cursor:
        trailer["cursor"] = cursor
    return 200, "application/x-ndjson", "".join(json.dumps(r) + "\n" for r in rows) + json.dumps(trailer) + "\n"


def _problem(status: int, code: str, detail: str) -> tuple[int, str, str]:
    return status, "application/problem+json", json.dumps(
        {"type": "about:blank", "title": code, "status": status, "detail": detail, "errorCode": code})


def _cosine(a: list[float], b: list[float]) -> float:
    dot = sum(x * y for x, y in zip(a, b))
    norm = math.sqrt(sum(x * x for x in a)) * math.sqrt(sum(y * y for y in b))
    return dot / norm if norm else 0.0
