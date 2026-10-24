/**
 * The Data Oil JavaScript and TypeScript client sample.
 *
 *     npm start              the public sandbox, read only - or your own tenant, when the
 *                            DATAOIL_* variables name it
 *     npm run dry-run        against a stub in this process, with no server at all
 *
 * With no DATAOIL_* variables set, the sample signs in to the public sandbox
 * (https://sandbox.dataoil.tech, database Sandbox) as its read-only account, lists the databases,
 * reads the schema, queries the World Bank sample table, reads one passage's translations, and
 * shows that a write is refused.
 *
 * With DATAOIL_URL and the rest set, it works in your own tenant: connect, create a database (when
 * asked to and allowed to), write notes, query them, search them by keyword and by meaning, and
 * leave nothing behind. See README.md for the variables.
 */

import { randomUUID } from "node:crypto";
import { pathToFileURL } from "node:url";
import { DataOilAuthorizationError, DataOilClient, DataOilError, DataOilNotFoundError, type Row } from "@dataoil/client";
import { StubEngine } from "./stub-engine.js";

/**
 * Four numbers per note - how much it is about drilling, pipelines, safety and money - written by
 * hand so the vector search can be followed by eye and needs no model. A real application stores
 * what its embedding model produced, and searches with the same model's vector for the question.
 */
const NOTES: Array<{ title: string; body: string; rating: number; embedding: number[] }> = [
  { title: "Casing failure on well 7", body: "The casing failed at 2300 metres during cementing, and the crew stopped drilling.", rating: 5, embedding: [0.9, 0.1, 0.4, 0.0] },
  { title: "Pipeline pigging schedule", body: "Pigging the export pipeline every second week keeps wax out of the line.", rating: 3, embedding: [0.0, 0.9, 0.1, 0.1] },
  { title: "Permit to work audit", body: "The audit found two permits signed after the work had started.", rating: 4, embedding: [0.1, 0.1, 0.9, 0.1] },
];

/**
 * The public sandbox. Its account is public by design: it can read the database 'Sandbox' and
 * nothing else, and every write is refused. The password holds '#' and '%'; as a JavaScript string
 * it needs no escaping, because the client puts it into the Basic auth header itself.
 */
export const SANDBOX = { url: "https://sandbox.dataoil.tech", database: "Sandbox", username: "suser", password: "hFn3dh#2%hn8A" } as const;

/** The variables that name a connection. Set none of them and the sample uses the sandbox. */
export const CONNECTION_VARIABLES = ["DATAOIL_URL", "DATAOIL_DATABASE", "DATAOIL_TOKEN", "DATAOIL_USERNAME", "DATAOIL_PASSWORD"];

/** Whether an address is the public sandbox, where the read-only walk-through is the one that runs. */
export function isSandbox(url: string): boolean {
  try {
    return new URL(url).hostname.toLowerCase() === new URL(SANDBOX.url).hostname;
  } catch {
    return false;
  }
}

/**
 * Where the sample connects and as whom. The only default that reaches a server is the public
 * sandbox, as its read-only account; name any connection variable and every one that is missing is
 * reported rather than filled in.
 */
export interface Settings {
  url: string;
  database: string;
  token?: string;
  username?: string;
  password?: string;
  createDatabase: boolean;
  jobId: string;
  sandbox: boolean;
}

export function settingsFromEnvironment(env: Record<string, string | undefined>): { settings?: Settings; problems: string[] } {
  const value = (name: string) => (env[name] ?? "").trim() || undefined;
  if (CONNECTION_VARIABLES.every((name) => value(name) === undefined)) {
    return {
      settings: { ...SANDBOX, createDatabase: false, jobId: value("DATAOIL_JOB_ID") ?? "sample-javascript-client", sandbox: true },
      problems: [],
    };
  }
  const problems: string[] = [];
  const url = value("DATAOIL_URL"), database = value("DATAOIL_DATABASE");
  const token = value("DATAOIL_TOKEN"), username = value("DATAOIL_USERNAME"), password = value("DATAOIL_PASSWORD");
  if (!url) problems.push("DATAOIL_URL is not set: the address the engine answers on, such as https://acme.dataoil.tech.");
  if (!database) problems.push("DATAOIL_DATABASE is not set: the database the sample works in.");
  if (Boolean(token) === Boolean(username)) problems.push("Set DATAOIL_TOKEN, or DATAOIL_USERNAME and DATAOIL_PASSWORD, and not both.");
  else if (username && !password) problems.push("DATAOIL_PASSWORD is not set for DATAOIL_USERNAME.");
  if (problems.length > 0) return { problems };
  return {
    settings: {
      url: url!, database: database!, token, username, password,
      createDatabase: (value("DATAOIL_CREATE_DATABASE") ?? "").toLowerCase() === "true",
      jobId: value("DATAOIL_JOB_ID") ?? "sample-javascript-client",
      sandbox: isSandbox(url!),
    },
    problems,
  };
}

export interface Result {
  createdDatabase: boolean;
  written: number;
  highlyRated: string[];
  streamed: number;
  keywordHit?: string;
  nearestHit?: string;
}

/** Every step, against the database the client names. */
export async function run(client: DataOilClient, createDatabase: boolean, log: (line: string) => void, typeName?: string): Promise<Result> {
  const type = typeName ?? `SampleNote_${randomUUID().replaceAll("-", "").slice(0, 8)}`;
  const database = encodeURIComponent(client.database);

  // 1. Connect. The schema version is the smallest answer that proves the address, the credential
  //    and the database; a refusal is a typed error carrying the engine's own code.
  let created = false;
  try {
    const answer = (await client.request("GET", `/api/v2/databases/${database}/schema/version`)) as { version: number };
    log(`1. Connected to ${client.database}: schema version ${answer.version}.`);
  } catch (failure) {
    if (!(failure instanceof DataOilNotFoundError) || !createDatabase) throw failure;
    // Creating a database needs the right to: a tenant administrator, or a role holding the
    // tenant's database capability. Anybody else is refused with a 403 that says so.
    await client.request("POST", "/api/v2/databases", { name: client.database });
    created = true;
    log(`1. Created the database ${client.database}.`);
  }

  try {
    try {
      // 2. A collection with declared properties, a keyword index and a vector index.
      for (const statement of [
        `CREATE DOCUMENT TYPE ${type}`,
        `CREATE PROPERTY ${type}.title STRING`,
        `CREATE PROPERTY ${type}.body STRING`,
        `CREATE PROPERTY ${type}.rating INTEGER`,
        `CREATE PROPERTY ${type}.embedding ARRAY_OF_FLOATS`,
        `CREATE INDEX ON ${type} (body) FULL_TEXT`,
        `CREATE INDEX ON ${type} (embedding) LSM_VECTOR METADATA {dimensions: 4, similarity: 'COSINE'}`,
      ]) {
        await client.statement(statement);
      }
      log(`2. Created the collection ${type}, with a full-text index on body and a vector index on embedding.`);

      // 3. Write: three notes in one batch (one transaction: all or none), and one more through the
      //    documents route, which answers the record with its identity.
      await client.batch(NOTES.map((note) => ({
        sql: `INSERT INTO ${type} SET title = :title, body = :body, rating = :rating, embedding = :embedding`,
        params: note,
      })));
      const record = await client.create(type, { title: "Rig day rate", body: "The day rate for the rig rises in March.", rating: 2, embedding: [0.3, 0.0, 0.0, 0.9] });
      const written = NOTES.length + 1;
      log(`3. Wrote ${NOTES.length} notes in one batch, and one more as record ${String(record.rid)}.`);

      // 4. Query: one page into an array, then the whole collection streamed two rows a page,
      //    following the cursor each page's trailer carries.
      const highlyRated = (await client.statement(`SELECT title, rating FROM ${type} WHERE rating >= :least ORDER BY rating DESC`, { least: 4 }))
        .map((row) => String(row.title));
      let streamed = 0;
      for await (const row of client.query(`SELECT title FROM ${type} ORDER BY title`, { pageSize: 2 })) {
        void row;
        streamed++;
      }
      log(`4. Rated 4 or more: ${highlyRated.join(", ")}. Streamed ${streamed} rows two at a time.`);

      // 5. Search: by keyword through the full-text index ($score is the BM25 relevance the index
      //    handed back), and by meaning through the vector index - the two notes nearest a question
      //    that is mostly about drilling and a little about safety.
      const byKeyword: Row[] = await client.statement(
        `SELECT title, $score AS score FROM ${type} WHERE SEARCH_INDEX('${type}[body]', :q) = true ORDER BY score DESC LIMIT 3`,
        { q: "casing cementing" });
      const byMeaning: Row[] = await client.statement(
        `SELECT title, VECTOR_DISTANCE(embedding, :v, 'cosine') AS distance FROM (SELECT expand(vector.neighbors('${type}[embedding]', :v, 2))) ORDER BY distance`,
        { v: [1.0, 0.0, 0.2, 0.0] });
      const keywordHit = byKeyword[0]?.title as string | undefined;
      const nearestHit = byMeaning[0]?.title as string | undefined;
      log(`5. Keyword search for 'casing cementing' ranked first: ${keywordHit ?? "(nothing)"}. Nearest by meaning: ${nearestHit ?? "(nothing)"}.`);

      return { createdDatabase: created, written, highlyRated, streamed, keywordHit, nearestHit };
    } finally {
      // 6. Clean up the collection, even when a step failed.
      await cleanUp(log, `the collection ${type}`, async () => {
        await client.statement(`DELETE FROM ${type}`);
        await client.statement(`DROP TYPE ${type}`);
      });
    }
  } finally {
    // ...and the database, but only when this run made it: one that was there before is somebody's.
    if (created) {
      await cleanUp(log, `the database ${client.database}, which this run created`, () => client.request("DELETE", `/api/v2/databases/${database}`));
    }
  }
}

export interface SandboxResult {
  databases: string[];
  collections: string[];
  columns: string[];
  topGdp: string[];
  japanRows: number;
  longestLived: string[];
  languages: string[];
  writeRefused?: string;
}

/** The read-only walk-through: what the sandbox account can do, and the write it cannot. */
export async function runSandbox(client: DataOilClient, log: (line: string) => void): Promise<SandboxResult> {
  const money = (value: unknown) => Number(value).toLocaleString("en-US", { maximumFractionDigits: 0 }).padStart(22);
  const second = (text: string) => (text.split("\n")[1] ?? text).slice(0, 80);

  // 1. The databases this account can see. `request` reaches any route the client has no method
  //    for, with the headers added and a refusal turned into a typed error.
  const { databases } = (await client.request("GET", "/api/v2/databases")) as { databases: string[] };
  log(`1. Databases: ${databases.join(", ")}`);

  // 2. The schema: the collections (types) this account may read, with their properties.
  type Type = { name: string; properties: Array<{ name: string; type: string }> };
  const types = ((await client.schema()) as { types: Type[] }).types.filter((type) => type.name !== "V" && type.name !== "E");
  const rowsType = types.find((type) => type.name === "WorldBank_rows")!;
  log(`2. Collections: ${types.map((type) => type.name).join(", ")}`);
  log(`   WorldBank_rows: ${rowsType.properties.map((p) => `${p.name} ${p.type}`).join(", ")}`);

  // 3a. Top GDP in one year. Column names with spaces or brackets are quoted with backticks; values
  //     go in as :name parameters, never pasted into the statement.
  log("3a. Top 5 GDP in 2015:");
  const top = await client.statement(
    "SELECT Country, `GDP (USD)` AS gdp FROM WorldBank_rows WHERE Year = :year ORDER BY `GDP (USD)` DESC LIMIT 5",
    { year: 2015 });
  for (const row of top) log(`    ${String(row.Country).padEnd(15)} ${money(row.gdp)}`);

  // 3b. One country's series. `query` streams the rows as they arrive and follows the cursor.
  log("3b. Japan, by year:");
  let japanRows = 0;
  for await (const row of client.query(
    "SELECT Year, `GDP (USD)` AS gdp, `Life Expectancy` AS life FROM WorldBank_rows WHERE Country = :country ORDER BY Year",
    { params: { country: "Japan" } })) {
    japanRows++;
    log(`    ${row.Year}  GDP ${money(row.gdp)}  life expectancy ${row.life}`);
  }

  // 3c. An aggregate: average life expectancy per country.
  log("3c. Average life expectancy per country (top 5):");
  const lived = await client.statement(
    "SELECT Country, avg(`Life Expectancy`) AS life, count(*) AS n FROM WorldBank_rows GROUP BY Country ORDER BY life DESC LIMIT 5");
  for (const row of lived) log(`    ${String(row.Country).padEnd(15)} ${Number(row.life).toFixed(1)}  (${row.n} rows)`);

  // 4. One passage of the table's description, with the translations made when it was loaded. Its
  //    second line lists the column names; show it in each language.
  const [passage] = await client.statement(
    "SELECT chunk_index, content, translations FROM WorldBank_chunks ORDER BY chunk_index LIMIT 1");
  log(`4. Passage ${passage.chunk_index}: ${second(String(passage.content))}`);
  const languages: string[] = [];
  for (const translation of (passage.translations ?? []) as Array<{ code: string; content: string }>) {
    languages.push(translation.code); // the language tag: <2es>, <2fr>, <2it>, <2ja>, <2zh>
    log(`    ${translation.code}  ${second(translation.content)}`);
  }

  const result: SandboxResult = {
    databases, collections: types.map((type) => type.name), columns: rowsType.properties.map((p) => p.name),
    topGdp: top.map((row) => String(row.Country)), japanRows, longestLived: lived.map((row) => String(row.Country)), languages,
  };

  // 5. A write is refused: the account is read only. The client throws a typed error carrying the
  //    engine's status, stable error code and correlation id.
  try {
    await client.statement("INSERT INTO WorldBank_rows SET Country = 'Atlantis', Year = 2020");
    log("5. The write was accepted - that should not happen on the sandbox.");
  } catch (failure) {
    if (!(failure instanceof DataOilAuthorizationError)) throw failure;
    result.writeRefused = `${failure.status} ${failure.errorCode}`;
    log(`5. Write refused as expected: ${failure.status} ${failure.errorCode} - ${failure.detail}`);
  }
  return result;
}

/** One clean-up step, reported rather than thrown, so it cannot hide the failure that led here. */
async function cleanUp(log: (line: string) => void, what: string, step: () => Promise<unknown>): Promise<void> {
  try {
    await step();
    log(`6. Dropped ${what}.`);
  } catch (failure) {
    if (!(failure instanceof DataOilError)) throw failure;
    log(`6. Could not drop ${what}: ${failure.errorCode} ${failure.detail}`);
  }
}

/** 0 when every step ran; 1 for missing settings; 2 when the engine refused something. */
export async function main(argv: string[], env: Record<string, string | undefined> = process.env, log = console.log, error = console.error): Promise<number> {
  let settings: Settings;
  let fetchImpl: typeof fetch | undefined;

  if (argv.includes("--dry-run")) {
    log("Dry run: the answers come from a stub in this process, not from an engine. Nothing is sent anywhere.");
    const stub = new StubEngine();
    fetchImpl = stub.fetch;
    settings = { url: "https://engine.invalid", database: "sample_notes", token: "dot_placeholder", createDatabase: true, jobId: "sample-javascript-client", sandbox: false };
  } else {
    const read = settingsFromEnvironment(env);
    if (!read.settings) {
      for (const problem of read.problems) error(problem);
      error("See README.md, or unset every DATAOIL_* variable to use the public sandbox, or run with --dry-run to see the sample work without a server.");
      return 1;
    }
    settings = read.settings;
  }

  const client = new DataOilClient({
    baseUrl: settings.url, database: settings.database, token: settings.token,
    username: settings.username, password: settings.password, jobId: settings.jobId, fetch: fetchImpl,
  });

  try {
    if (settings.sandbox) {
      log(`The public sandbox: ${settings.url}, database ${settings.database}, as the read-only account ${settings.username}. Set DATAOIL_URL and the rest to use your own tenant.`);
      const sandboxed = await runSandbox(client, log);
      if (!sandboxed.writeRefused) return 2;
      log("Done: a read-only walk-through of the sandbox; nothing was written.");
      return 0;
    }
    const result = await run(client, settings.createDatabase, log);
    log(`Done: ${result.written} notes written, searched and cleaned up.`);
    return 0;
  } catch (failure) {
    if (!(failure instanceof DataOilError)) throw failure;
    // Every refusal carries a stable code, the engine's own sentence and a correlation id that the
    // engine's logs and the tenant's activity view are indexed by.
    error(`The engine refused: ${failure.status} ${failure.errorCode}: ${failure.detail} (correlation ${failure.correlationId})`);
    return 2;
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exitCode = await main(process.argv.slice(2));
}
