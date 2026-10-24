/**
 * The Data Oil MCP server sample: an MCP client that starts `dataoil-mcp` exactly as an assistant
 * host or an agent framework does - a child process speaking the protocol on stdio - and drives its
 * tools the way a model would.
 *
 *     npm start              the public sandbox, read only - or your own tenant, when the
 *                            DATAOIL_* variables name it
 *     npm run dry-run        the server in this process, against a stub, with no engine at all
 *
 * With no DATAOIL_* variables set, the server is started against the public sandbox
 * (https://sandbox.dataoil.tech, database Sandbox) as its read-only account: the sample lists the
 * tools, reads the schema both ways, queries the World Bank sample table and one passage's
 * translations, and shows that `ingest` is refused.
 *
 * With DATAOIL_URL and the rest set, it works in your own tenant: define a collection, write, read,
 * search and clean up, all through the tools.
 *
 * The tools are `query`, `schema`, `ingest` and `ask`. This sample uses the first three; `ask`
 * sends rows to the tenant's own model endpoint, which a sample cannot assume is configured (the
 * sandbox has none). See README.md for the variables, and for the configuration an MCP host takes.
 */

import { randomUUID } from "node:crypto";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";
import { DataOilClient, DataOilError, DataOilNotFoundError } from "@dataoil/client";
import { createServer } from "@dataoil/mcp";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport, getDefaultEnvironment } from "@modelcontextprotocol/sdk/client/stdio.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { StubEngine } from "./stub-engine.js";

/** Hand-written four-number vectors - drilling, pipelines, safety, money - so the search needs no model. */
const NOTES = [
  { title: "Casing failure on well 7", body: "The casing failed at 2300 metres during cementing, and the crew stopped drilling.", rating: 5, embedding: [0.9, 0.1, 0.4, 0.0] },
  { title: "Pipeline pigging schedule", body: "Pigging the export pipeline every second week keeps wax out of the line.", rating: 3, embedding: [0.0, 0.9, 0.1, 0.1] },
  { title: "Permit to work audit", body: "The audit found two permits signed after the work had started.", rating: 4, embedding: [0.1, 0.1, 0.9, 0.1] },
];

/**
 * The public sandbox. Its account is public by design: it can read the database 'Sandbox' and
 * nothing else, and every write is refused. The password holds '#' and '%'; as a JavaScript string,
 * and as a value in an MCP host's JSON configuration, it needs no escaping.
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
 * Where the server connects and as whom. The only default that reaches a server is the public
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
      settings: { ...SANDBOX, createDatabase: false, jobId: value("DATAOIL_JOB_ID") ?? "sample-mcp", sandbox: true },
      problems: [],
    };
  }
  const problems: string[] = [];
  const url = value("DATAOIL_URL"), database = value("DATAOIL_DATABASE");
  const token = value("DATAOIL_TOKEN"), username = value("DATAOIL_USERNAME"), password = value("DATAOIL_PASSWORD");
  if (!url) problems.push("DATAOIL_URL is not set: the address the engine answers on, such as https://acme.dataoil.tech.");
  if (!database) problems.push("DATAOIL_DATABASE is not set: the database the server works in.");
  if (Boolean(token) === Boolean(username)) problems.push("Set DATAOIL_TOKEN, or DATAOIL_USERNAME and DATAOIL_PASSWORD, and not both.");
  else if (username && !password) problems.push("DATAOIL_PASSWORD is not set for DATAOIL_USERNAME.");
  if (problems.length > 0) return { problems };
  return {
    settings: {
      url: url!, database: database!, token, username, password,
      createDatabase: (value("DATAOIL_CREATE_DATABASE") ?? "").toLowerCase() === "true",
      jobId: value("DATAOIL_JOB_ID") ?? "sample-mcp",
      sandbox: isSandbox(url!),
    },
    problems,
  };
}

export interface Result {
  tools: string[];
  written: number;
  highlyRated: string[];
  keywordHit?: string;
  nearestHit?: string;
}

/** A tool's answer, as the model would read it: the JSON in its text, or the refusal it carries. */
async function call(mcp: Client, name: string, args: Record<string, unknown>): Promise<any> {
  const answer = (await mcp.callTool({ name, arguments: args })) as { isError?: boolean; content: Array<{ type: string; text: string }> };
  const text = answer.content[0]?.text ?? "";
  if (answer.isError) throw new Error(`${name} was refused: ${text}`);
  try {
    return JSON.parse(text);
  } catch {
    return text; // the schema in prose
  }
}

/** Every step, through the server's tools. */
export async function run(mcp: Client, log: (line: string) => void, typeName?: string): Promise<Result> {
  const type = typeName ?? `SampleNote_${randomUUID().replaceAll("-", "").slice(0, 8)}`;

  // 1. What the server offers - what a host shows the model.
  const { tools } = await mcp.listTools();
  const names = tools.map((tool) => tool.name).sort();
  log(`2. The server offers: ${names.join(", ")}.`);

  try {
    // 2. Define the collection through `query`, which takes DDL as well as reads and writes.
    for (const sql of [
      `CREATE DOCUMENT TYPE ${type}`,
      `CREATE PROPERTY ${type}.title STRING`,
      `CREATE PROPERTY ${type}.body STRING`,
      `CREATE PROPERTY ${type}.rating INTEGER`,
      `CREATE PROPERTY ${type}.embedding ARRAY_OF_FLOATS`,
      `CREATE INDEX ON ${type} (body) FULL_TEXT`,
      `CREATE INDEX ON ${type} (embedding) LSM_VECTOR METADATA {dimensions: 4, similarity: 'COSINE'}`,
    ]) {
      await call(mcp, "query", { sql });
    }
    log(`3. query: created the collection ${type}, with a full-text index on body and a vector index on embedding.`);

    // 3. Write through `ingest`: one record per object.
    const ingested = await call(mcp, "ingest", { collection: type, records: NOTES });
    log(`4. ingest: wrote ${ingested.written} records.`);

    // 4. `schema`, as the server's instructions tell a model to call before it writes a query.
    const schema = await call(mcp, "schema", { asTypes: true });
    const listed = (schema.types ?? []).some((t: { name: string }) => t.name === type);
    log(`5. schema: ${type} is ${listed ? "listed" : "not listed"}.`);

    // 5. Read and search through `query`; the answer is capped at DATAOIL_MAX_ROWS rows.
    const rated = await call(mcp, "query", { sql: `SELECT title, rating FROM ${type} WHERE rating >= :least ORDER BY rating DESC`, params: { least: 4 } });
    const highlyRated = rated.rows.map((row: { title: string }) => row.title);
    const keyword = await call(mcp, "query", {
      sql: `SELECT title, $score AS score FROM ${type} WHERE SEARCH_INDEX('${type}[body]', :q) = true ORDER BY score DESC LIMIT 3`,
      params: { q: "casing cementing" },
    });
    const nearest = await call(mcp, "query", {
      sql: `SELECT title, VECTOR_DISTANCE(embedding, :v, 'cosine') AS distance FROM (SELECT expand(vector.neighbors('${type}[embedding]', :v, 2))) ORDER BY distance`,
      params: { v: [1.0, 0.0, 0.2, 0.0] },
    });
    const keywordHit: string | undefined = keyword.rows[0]?.title;
    const nearestHit: string | undefined = nearest.rows[0]?.title;
    log(`6. query: rated 4 or more: ${highlyRated.join(", ")}. Keyword search for 'casing cementing' ranked first: ${keywordHit ?? "(nothing)"}. Nearest by meaning: ${nearestHit ?? "(nothing)"}.`);

    return { tools: names, written: ingested.written, highlyRated, keywordHit, nearestHit };
  } finally {
    // 6. Clean up through `query`, even when a step failed.
    try {
      await call(mcp, "query", { sql: `DELETE FROM ${type}` });
      await call(mcp, "query", { sql: `DROP TYPE ${type}` });
      log(`7. Dropped the collection ${type}.`);
    } catch (failure) {
      log(`7. Could not drop the collection ${type}: ${failure instanceof Error ? failure.message : String(failure)}`);
    }
  }
}

export interface SandboxResult {
  tools: string[];
  collections: string[];
  prose: string;
  topGdp: string[];
  languages: string[];
  ingestRefused?: string;
}

/** The read-only walk-through, through the tools: what the sandbox account can do, and the write it cannot. */
export async function runSandbox(mcp: Client, log: (line: string) => void): Promise<SandboxResult> {
  const short = (value: string, length: number) => (value.length > length ? value.slice(0, length) + " ..." : value);

  // 1. What the server offers - what a host shows the model.
  const { tools } = await mcp.listTools();
  const names = tools.map((tool) => tool.name).sort();
  log(`1. tools/list: ${names.join(", ")}`);

  // 2. `schema` as types: the collections this account may read, with their properties.
  const schema = await call(mcp, "schema", { asTypes: true });
  const collections: string[] = (schema.types ?? []).map((t: { name: string }) => t.name).filter((n: string) => n !== "V" && n !== "E");
  log(`2. schema {asTypes: true}: ${collections.length} collections - ${collections.join(", ")}`);

  // 3. `schema` in prose (the default): the engine's own description of the database, which is
  //    what the server's instructions tell a model to read before it writes a query.
  const prose = String(await call(mcp, "schema", {}));
  log(`3. schema {} (prose): ${short(prose.replace(/\s*\n\s*/g, "; "), 110)}`);

  // 4. `query`: one statement, rows back. Backtick-quoted columns, a :year parameter.
  const top = await call(mcp, "query", {
    sql: "SELECT Country, `GDP (USD)` AS gdp FROM WorldBank_rows WHERE Year = :year ORDER BY `GDP (USD)` DESC LIMIT 3",
    params: { year: 2015 },
  });
  const topGdp: string[] = top.rows.map((row: { Country: string }) => row.Country);
  log(`4. query: top 3 GDP in 2015 - ${top.rows.map((row: { Country: string; gdp: number }) => `${row.Country} ${row.gdp}`).join("; ")}`);

  // 5. `query` over the passages: one passage and the translations made when it was loaded.
  const passage = (await call(mcp, "query", {
    sql: "SELECT chunk_index, content, translations FROM WorldBank_chunks ORDER BY chunk_index LIMIT 1",
  })).rows[0];
  const languages: string[] = (passage.translations ?? []).map((t: { code: string }) => t.code);
  const second = (text: string) => short(text.split("\n")[1] ?? text, 60);
  log(`5. query: passage ${passage.chunk_index} in ${languages.join(" ")} - ${(passage.translations ?? []).slice(0, 2).map((t: { code: string; content: string }) => `${t.code} ${second(t.content)}`).join(" | ")}`);

  // 6. `ingest` is refused: the account is read only. The refusal comes back as the tool's error,
  //    carrying the engine's code, so the model is told why rather than left to guess.
  const result: SandboxResult = { tools: names, collections, prose, topGdp, languages };
  const ingest = (await mcp.callTool({
    name: "ingest",
    arguments: { collection: "WorldBank_rows", records: [{ Country: "Atlantis", Year: 2020 }] },
  })) as { isError?: boolean; content: Array<{ text: string }> };
  if (ingest.isError) {
    const refusal = JSON.parse(ingest.content[0]?.text ?? "{}") as { error?: string; status?: number; detail?: string };
    result.ingestRefused = `${refusal.status} ${refusal.error}`;
    log(`6. ingest refused as expected: ${refusal.status} ${refusal.error} - ${refusal.detail}`);
  } else {
    log("6. ingest was accepted - that should not happen on the sandbox.");
  }
  return result;
}

/**
 * The server is scoped to one database and has no tool that creates one, so a database the sample
 * is asked to create is created - and dropped at the end - with `@dataoil/client`, which the
 * server is built on.
 */
async function ensureDatabase(client: DataOilClient, createDatabase: boolean, log: (line: string) => void): Promise<boolean> {
  try {
    await client.request("GET", `/api/v2/databases/${encodeURIComponent(client.database)}/schema/version`);
    log(`1. ${client.database} is there.`);
    return false;
  } catch (failure) {
    if (!(failure instanceof DataOilNotFoundError) || !createDatabase) throw failure;
    await client.request("POST", "/api/v2/databases", { name: client.database });
    log(`1. Created the database ${client.database}.`);
    return true;
  }
}

/** 0 when every step ran; 1 for missing settings; 2 when the engine or the server refused something. */
export async function main(argv: string[], env: Record<string, string | undefined> = process.env, log = console.log, error = console.error): Promise<number> {
  const dryRun = argv.includes("--dry-run");
  let settings: Settings;
  let stub: StubEngine | undefined;

  if (dryRun) {
    log("Dry run: the server runs in this process against a stub, not an engine. Nothing is sent anywhere.");
    stub = new StubEngine();
    settings = { url: "https://engine.invalid", database: "sample_notes", token: "dot_placeholder", createDatabase: true, jobId: "sample-mcp", sandbox: false };
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
    username: settings.username, password: settings.password, jobId: settings.jobId, fetch: stub?.fetch,
  });
  const mcp = new Client({ name: "dataoil-sample", version: "1.0.0" });
  let created = false;

  try {
    if (settings.sandbox) {
      log(`The public sandbox: ${settings.url}, database ${settings.database}, as the read-only account ${settings.username}. Set DATAOIL_URL and the rest to use your own tenant.`);
    } else {
      created = await ensureDatabase(client, settings.createDatabase, log);
    }

    if (stub) {
      // The server in this process, joined to the client by an in-memory pair of transports.
      const server = createServer({ url: settings.url, database: settings.database, token: settings.token, jobId: settings.jobId, fetch: stub.fetch });
      const [clientSide, serverSide] = InMemoryTransport.createLinkedPair();
      await server.connect(serverSide);
      await mcp.connect(clientSide);
    } else {
      // The server as a host starts it: a child process on stdio, configured by its environment.
      // Only the variables it needs are passed on, beside the platform's defaults: the settings
      // (the sandbox's, or the ones read from the environment), and two tuning variables.
      const cli = createRequire(import.meta.url).resolve("@dataoil/mcp/dist/cli.js");
      const passed: Record<string, string> = { ...getDefaultEnvironment(), DATAOIL_URL: settings.url, DATAOIL_DATABASE: settings.database, DATAOIL_JOB_ID: settings.jobId };
      if (settings.token) passed.DATAOIL_TOKEN = settings.token;
      if (settings.username) passed.DATAOIL_USERNAME = settings.username;
      if (settings.password) passed.DATAOIL_PASSWORD = settings.password;
      for (const name of ["DATAOIL_MAX_ROWS", "NODE_EXTRA_CA_CERTS"]) {
        const value = env[name];
        if (value) passed[name] = value;
      }
      await mcp.connect(new StdioClientTransport({ command: process.execPath, args: [cli], env: passed }));
    }

    if (settings.sandbox) {
      const sandboxed = await runSandbox(mcp, log);
      if (!sandboxed.ingestRefused) return 2;
      log("Done: a read-only walk-through of the sandbox through the server's tools; nothing was written.");
      return 0;
    }

    const result = await run(mcp, log);
    log(`Done: ${result.written} records written through the server, queried, searched and cleaned up.`);
    return 0;
  } catch (failure) {
    if (failure instanceof DataOilError) {
      error(`The engine refused: ${failure.status} ${failure.errorCode}: ${failure.detail} (correlation ${failure.correlationId})`);
      return 2;
    }
    if (failure instanceof Error && failure.message.includes("was refused")) {
      error(failure.message);
      return 2;
    }
    throw failure;
  } finally {
    await mcp.close();
    if (created) {
      try {
        await client.request("DELETE", `/api/v2/databases/${encodeURIComponent(client.database)}`);
        log(`7. Dropped the database ${client.database}, which this run created.`);
      } catch (failure) {
        if (!(failure instanceof DataOilError)) throw failure;
        log(`7. Could not drop the database ${client.database}: ${failure.errorCode} ${failure.detail}`);
      }
    }
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exitCode = await main(process.argv.slice(2));
}
