// The sample against the server in this process and a stub engine: no server, no credential,
// nothing sent anywhere. What is asserted is what an MCP client receives through the protocol.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "@dataoil/mcp";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { main, run, runSandbox, settingsFromEnvironment } from "../dist/sample.js";
import { StubEngine } from "../dist/stub-engine.js";
import { SandboxStub } from "../dist/sandbox-stub.js";

const quiet = () => {};

async function connected(stub) {
  const server = createServer({ url: "https://engine.invalid", database: "sample_notes", token: "dot_placeholder", jobId: "sample-tests", fetch: stub.fetch });
  const [clientSide, serverSide] = InMemoryTransport.createLinkedPair();
  await server.connect(serverSide);
  const mcp = new Client({ name: "sample-tests", version: "0" });
  await mcp.connect(clientSide);
  return mcp;
}

test("every tool step runs and answers what was written", async () => {
  const stub = new StubEngine(true);
  const mcp = await connected(stub);
  const result = await run(mcp, quiet, "SampleNote_test");
  await mcp.close();
  assert.deepEqual(result.tools, ["ask", "ingest", "query", "schema"]);
  assert.equal(result.written, 3);
  assert.deepEqual(result.highlyRated, ["Casing failure on well 7", "Permit to work audit"]);
  assert.equal(result.keywordHit, "Casing failure on well 7");
  assert.equal(result.nearestHit, "Casing failure on well 7");
  assert.equal(stub.everyRequestCarriedTheHeaders, true);
});

test("the collection is dropped at the end", async () => {
  const stub = new StubEngine(true);
  const mcp = await connected(stub);
  await run(mcp, quiet, "SampleNote_test");
  const schema = await mcp.callTool({ name: "schema", arguments: { asTypes: true } });
  await mcp.close();
  assert.ok(!schema.content[0].text.includes("SampleNote_test"));
});

test("the dry run creates and drops its database and exits cleanly", async () => {
  const lines = [];
  assert.equal(await main(["--dry-run"], {}, (line) => lines.push(line), quiet), 0);
  assert.ok(lines.some((line) => line.startsWith("1. Created the database sample_notes")));
  assert.ok(lines.some((line) => line.startsWith("7. Dropped the database sample_notes")));
});

test("the sandbox walk-through reads through the tools, and ingest is refused", async () => {
  const stub = new SandboxStub();
  const server = createServer({ url: "https://sandbox.invalid", database: "Sandbox", username: "suser", password: "placeholder", jobId: "sample-tests", fetch: stub.fetch });
  const [clientSide, serverSide] = InMemoryTransport.createLinkedPair();
  await server.connect(serverSide);
  const mcp = new Client({ name: "sample-tests", version: "0" });
  await mcp.connect(clientSide);
  const lines = [];
  const result = await runSandbox(mcp, (line) => lines.push(line));
  await mcp.close();
  assert.deepEqual(result.tools, ["ask", "ingest", "query", "schema"]);
  assert.ok(result.collections.includes("WorldBank_rows"));
  assert.ok(!result.collections.includes("V"));
  assert.match(result.prose, /WorldBank_rows/);
  assert.deepEqual(result.topGdp, ["Italy", "Nigeria", "India"]);
  assert.deepEqual(result.languages, ["<2es>", "<2fr>"]);
  assert.equal(result.ingestRefused, "403 Security");
  assert.ok(lines.some((line) => line.startsWith("6. ingest refused as expected: 403 Security")));
  assert.equal(stub.everyRequestCarriedTheHeaders, true);
});

test("no variables mean the public sandbox, read only", () => {
  const read = settingsFromEnvironment({});
  assert.deepEqual(read.problems, []);
  assert.equal(read.settings.sandbox, true);
  assert.equal(read.settings.url, "https://sandbox.dataoil.tech");
  assert.equal(read.settings.database, "Sandbox");
  assert.equal(read.settings.username, "suser");
  assert.equal(read.settings.token, undefined);
  assert.equal(read.settings.createDatabase, false);
});

test("naming any connection variable leaves the sandbox and names the rest", async () => {
  const read = settingsFromEnvironment({ DATAOIL_URL: "https://acme.dataoil.tech" });
  assert.equal(read.settings, undefined);
  assert.equal(read.problems.length, 2);
  assert.equal(await main([], { DATAOIL_TOKEN: "dot_placeholder" }, quiet, quiet), 1);
});

test("your own tenant runs the write walk-through; the sandbox named explicitly is still read only", () => {
  assert.equal(settingsFromEnvironment({ DATAOIL_URL: "https://acme.dataoil.tech", DATAOIL_DATABASE: "sample_notes", DATAOIL_TOKEN: "dot_placeholder" }).settings.sandbox, false);
  assert.equal(settingsFromEnvironment({ DATAOIL_URL: "https://SANDBOX.dataoil.tech/", DATAOIL_DATABASE: "Sandbox", DATAOIL_USERNAME: "suser", DATAOIL_PASSWORD: "placeholder" }).settings.sandbox, true);
});

test("a token and a name together are refused", () => {
  const read = settingsFromEnvironment({ DATAOIL_URL: "https://acme.dataoil.tech", DATAOIL_DATABASE: "sample_notes", DATAOIL_TOKEN: "dot_placeholder", DATAOIL_USERNAME: "someone" });
  assert.equal(read.settings, undefined);
  assert.match(read.problems[0], /not both/);
});
