/**
 * A stand-in for the engine, for the dry run and the tests: a `fetch` the client sends through,
 * answering the requests this sample sends in the shapes the engine answers them. No socket is
 * opened and nothing is sent anywhere.
 *
 * It is not an engine. It keeps the notes the sample writes and works the sample's own questions
 * out from them - a rating filter, a word match for the keyword search, cosine distance for the
 * vector search - so a dry run prints answers that follow from what was written. A statement it
 * does not recognise is a 400, so a sample that drifts from what the stub knows fails its test
 * rather than passing on a guess.
 */

type Note = Record<string, unknown> & { title: string; body: string; rating: number; embedding: number[] };

export class StubEngine {
  readonly requests: string[] = [];
  everyRequestCarriedTheHeaders = true;
  private readonly notes: Note[] = [];
  private schemaVersion = 0;

  constructor(public databaseExists = false) {}

  /** The `fetch` to hand the client. */
  readonly fetch: typeof fetch = async (input, init) => {
    const url = new URL(String(input));
    const method = init?.method ?? "GET";
    const headers = (init?.headers ?? {}) as Record<string, string>;
    this.requests.push(`${method} ${url.pathname}`);
    this.everyRequestCarriedTheHeaders &&= Boolean(headers["X-Job-ID"] && headers["X-Database"] && headers.Authorization);
    const body = typeof init?.body === "string" && init.body.length > 0 ? JSON.parse(init.body) : undefined;
    return this.answer(method, url.pathname, body);
  };

  private answer(method: string, path: string, body: any): Response {
    if (method === "POST" && path === "/api/v2/databases") {
      this.databaseExists = true;
      return json(201, { name: body.name });
    }
    if (!this.databaseExists) return problem(404, "DatabaseNotFound", "There is no database by that name that this caller may see.");
    if (method === "DELETE" && /^\/api\/v2\/databases\/[^/]+$/.test(path)) {
      this.databaseExists = false;
      return new Response(null, { status: 204 });
    }
    if (method === "GET" && path.endsWith("/schema/version")) return json(200, { version: this.schemaVersion });
    if (method === "POST" && path.endsWith("/batch")) {
      return json(200, { results: body.operations.map((o: { params: Note }) => this.insert(o.params)) });
    }
    if (method === "POST" && path.endsWith("/documents")) {
      return json(201, { rid: this.insert(body.properties), type: body.type, properties: body.properties });
    }
    if (method === "POST" && path.endsWith("/query")) return this.statement(body);
    return problem(404, "RouteNotFound", `The stub does not answer ${method} ${path}.`);
  }

  private statement(body: { query: string; params?: any; pageSize?: number; cursor?: string }): Response {
    const sql = body.query;
    const params = body.params ?? {};
    if (sql.startsWith("CREATE ") || sql.startsWith("DROP ")) {
      this.schemaVersion++;
      return rows([{ changed: true }]);
    }
    if (sql.startsWith("DELETE FROM ")) {
      const count = this.notes.length;
      this.notes.length = 0;
      return rows([{ count }]);
    }
    if (sql.includes("SEARCH_INDEX(")) {
      const words = String(params.q).toLowerCase().split(/\s+/);
      return rows(this.notes
        .map((note) => ({ note, score: words.filter((w) => note.body.toLowerCase().includes(w)).length }))
        .filter((s) => s.score > 0)
        .sort((a, b) => b.score - a.score)
        .slice(0, 3)
        .map((s) => ({ title: s.note.title, score: s.score })));
    }
    if (sql.includes("vector.neighbors(")) {
      return rows(this.notes
        .map((note) => ({ note, distance: 1 - cosine(params.v, note.embedding) }))
        .sort((a, b) => a.distance - b.distance)
        .slice(0, 2)
        .map((s) => ({ title: s.note.title, distance: s.distance })));
    }
    if (sql.includes("WHERE rating >= :least")) {
      return rows(this.notes
        .filter((n) => n.rating >= params.least)
        .sort((a, b) => b.rating - a.rating)
        .map((n) => ({ title: n.title, rating: n.rating })));
    }
    if (sql.includes("ORDER BY title")) {
      // Paged as the engine pages: pageSize rows, and a cursor while rows remain.
      const size = body.pageSize ?? 500;
      const start = body.cursor ? Number(body.cursor.split("-")[1]) : 0;
      const ordered = [...this.notes].sort((a, b) => (a.title < b.title ? -1 : 1));
      const page = ordered.slice(start, start + size);
      const more = start + page.length < ordered.length;
      return rows(page.map((n) => ({ title: n.title })), more, more ? `stub-${start + page.length}` : undefined);
    }
    return problem(400, "StatementNotRecognised", `The stub does not know the statement: ${sql}`);
  }

  private insert(properties: Note): string {
    this.notes.push({ ...properties });
    return `#12:${this.notes.length - 1}`;
  }
}

function rows(list: Record<string, unknown>[], more = false, cursor?: string): Response {
  const trailer: Record<string, unknown> = { returned: list.length, more };
  if (cursor) trailer.cursor = cursor;
  const text = list.map((r) => JSON.stringify(r) + "\n").join("") + JSON.stringify(trailer) + "\n";
  return new Response(text, { status: 200, headers: { "content-type": "application/x-ndjson" } });
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function problem(status: number, code: string, detail: string): Response {
  return new Response(JSON.stringify({ type: "about:blank", title: code, status, detail, errorCode: code }), {
    status,
    headers: { "content-type": "application/problem+json" },
  });
}

function cosine(a: number[], b: number[]): number {
  let dot = 0, left = 0, right = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    left += a[i] * a[i];
    right += b[i] * b[i];
  }
  return dot / (Math.sqrt(left) * Math.sqrt(right));
}
