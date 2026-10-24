/**
 * A stand-in for the public sandbox, for the tests: a `fetch` the client sends through, holding a
 * few World Bank rows and one passage with its translations, for a read-only account - every write
 * is refused with 403 `Security`, as the sandbox refuses it. No socket is opened and nothing is sent
 * anywhere.
 *
 * Like the engine stub it recognises only the statements the sample sends; anything else is a 400,
 * so a sample that drifts from what the stub knows fails its test rather than passing on a guess.
 */

const ROWS = [
  { row_index: 0, Country: "Italy", Year: 2015, "GDP (USD)": 19900750000000, "Life Expectancy": 71.2 },
  { row_index: 1, Country: "Nigeria", Year: 2015, "GDP (USD)": 15033350000000, "Life Expectancy": 55.0 },
  { row_index: 2, Country: "India", Year: 2015, "GDP (USD)": 13691740000000, "Life Expectancy": 69.5 },
  { row_index: 3, Country: "Japan", Year: 2010, "GDP (USD)": 8351720000000, "Life Expectancy": 83.5 },
  { row_index: 4, Country: "Japan", Year: 2015, "GDP (USD)": 8922760000000, "Life Expectancy": 68.7 },
  { row_index: 5, Country: "Turkey", Year: 2012, "GDP (USD)": 5120000000000, "Life Expectancy": 70.6 },
];

const COLUMNS: Array<[string, string]> = [
  ["row_index", "LONG"], ["Country", "STRING"], ["Year", "LONG"], ["GDP (USD)", "DOUBLE"], ["Population", "DOUBLE"],
  ["Life Expectancy", "DOUBLE"], ["Unemployment Rate (%)", "DOUBLE"], ["CO2 Emissions (metric tons per capita)", "DOUBLE"],
  ["Access to Electricity (%)", "DOUBLE"],
];

const PASSAGE = {
  chunk_index: 0,
  content: "World Bank indicators\ncolumns: Country, Year, GDP (USD), Population, Life Expectancy",
  translations: [
    { code: "<2es>", content: "Indicadores del Banco Mundial\ncolumnas: País, Año, PIB (USD), Población" },
    { code: "<2fr>", content: "Indicateurs de la Banque mondiale\ncolonnes: Pays, Année, PIB (USD), Population" },
  ],
};

export class SandboxStub {
  readonly requests: string[] = [];
  everyRequestCarriedTheHeaders = true;

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
    if (method === "GET" && path === "/api/v2/databases") return json(200, { databases: ["Sandbox"] });
    if (method === "GET" && path.endsWith("/schema/narrative")) {
      return new Response("collection WorldBank_rows: one row per country and year, with GDP, population and life expectancy.", {
        status: 200,
        headers: { "content-type": "text/plain" },
      });
    }
    if (method === "GET" && path.endsWith("/schema")) {
      const types = ["V", "E", "Glossary", "WorldBank", "WorldBank_rows", "WorldBank_chunks"].map((name) => ({
        name,
        description: null,
        properties: name === "WorldBank_rows" ? COLUMNS.map(([column, type]) => ({ name: column, type })) : [],
      }));
      return json(200, { types });
    }
    if (method === "POST" && path.endsWith("/query")) return this.read(body);
    return refused();
  }

  private read(body: { query?: string; command?: string; params?: any }): Response {
    const sql = body.query ?? body.command ?? "";
    const params = body.params ?? {};
    if (!/^\s*SELECT/i.test(sql)) return refused();
    const limit = Number(/LIMIT (\d+)/.exec(sql)?.[1] ?? 500);
    if (sql.includes("WHERE Year = :year")) {
      return rows(ROWS.filter((r) => r.Year === params.year)
        .sort((a, b) => b["GDP (USD)"] - a["GDP (USD)"])
        .slice(0, limit)
        .map((r) => ({ Country: r.Country, gdp: r["GDP (USD)"] })));
    }
    if (sql.includes("WHERE Country = :country")) {
      return rows(ROWS.filter((r) => r.Country === params.country)
        .sort((a, b) => a.Year - b.Year)
        .map((r) => ({ Year: r.Year, gdp: r["GDP (USD)"], life: r["Life Expectancy"] })));
    }
    if (sql.includes("GROUP BY Country")) {
      const countries = new Map<string, number[]>();
      for (const r of ROWS) countries.set(r.Country, [...(countries.get(r.Country) ?? []), r["Life Expectancy"]]);
      return rows([...countries]
        .map(([Country, lives]) => ({ Country, life: lives.reduce((a, b) => a + b, 0) / lives.length, n: lives.length }))
        .sort((a, b) => b.life - a.life)
        .slice(0, 5));
    }
    if (sql.includes("FROM WorldBank_chunks")) return rows([PASSAGE]);
    return problem(400, "StatementNotRecognised", `The stub does not know the statement: ${sql}`);
  }
}

function refused(): Response {
  return problem(403, "Security", "'suser' may not do that in 'Sandbox'");
}

function rows(list: Record<string, unknown>[]): Response {
  const text = list.map((r) => JSON.stringify(r) + "\n").join("") + JSON.stringify({ returned: list.length, more: false }) + "\n";
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
