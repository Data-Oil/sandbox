// The sample against the stub: no server, no credential, nothing sent anywhere.
import { test } from "node:test";
import assert from "node:assert/strict";
import { DataOilClient, DataOilNotFoundError } from "@dataoil/client";
import { main, run, runSandbox, settingsFromEnvironment } from "../dist/sample.js";
import { StubEngine } from "../dist/stub-engine.js";
import { SandboxStub } from "../dist/sandbox-stub.js";

const client = (stub) => new DataOilClient({ baseUrl: "https://engine.invalid", database: "sample_notes", token: "dot_placeholder", jobId: "sample-tests", fetch: stub.fetch });
const quiet = () => {};

test("every step runs and answers what was written", async () => {
  const stub = new StubEngine();
  const result = await run(client(stub), true, quiet, "SampleNote_test");
  assert.equal(result.createdDatabase, true);
  assert.equal(result.written, 4);
  assert.deepEqual(result.highlyRated, ["Casing failure on well 7", "Permit to work audit"]);
  assert.equal(result.streamed, 4);
  assert.equal(result.keywordHit, "Casing failure on well 7");
  assert.equal(result.nearestHit, "Casing failure on well 7");
  assert.equal(stub.everyRequestCarriedTheHeaders, true);
});

test("the collection and a database the run created are dropped", async () => {
  const stub = new StubEngine();
  await run(client(stub), true, quiet, "SampleNote_test");
  assert.equal(stub.requests[1], "POST /api/v2/databases");
  assert.equal(stub.requests.at(-1), "DELETE /api/v2/databases/sample_notes");
  assert.equal(stub.databaseExists, false);
});

test("a database that was already there is never dropped", async () => {
  const stub = new StubEngine(true);
  const result = await run(client(stub), true, quiet, "SampleNote_test");
  assert.equal(result.createdDatabase, false);
  assert.ok(!stub.requests.includes("POST /api/v2/databases"));
  assert.ok(!stub.requests.some((r) => r.startsWith("DELETE /api/v2/databases")));
});

test("a missing database is not created unless asked", async () => {
  const stub = new StubEngine();
  await assert.rejects(run(client(stub), false, quiet, "SampleNote_test"), DataOilNotFoundError);
  assert.deepEqual(stub.requests, ["GET /api/v2/databases/sample_notes/schema/version"]);
});

test("the dry run exits cleanly", async () => {
  const lines = [];
  assert.equal(await main(["--dry-run"], {}, (line) => lines.push(line), quiet), 0);
  assert.ok(lines.at(-1).startsWith("Done: 4 notes written"));
});

const sandboxClient = (stub) => new DataOilClient({ baseUrl: "https://sandbox.invalid", database: "Sandbox", username: "suser", password: "placeholder", jobId: "sample-tests", fetch: stub.fetch });

test("the sandbox walk-through reads, and the write is refused", async () => {
  const stub = new SandboxStub();
  const lines = [];
  const result = await runSandbox(sandboxClient(stub), (line) => lines.push(line));
  assert.deepEqual(result.databases, ["Sandbox"]);
  assert.ok(result.collections.includes("WorldBank_rows"));
  assert.ok(!result.collections.includes("V"));
  assert.ok(result.columns.includes("GDP (USD)"));
  assert.deepEqual(result.topGdp, ["Italy", "Nigeria", "India", "Japan"]);
  assert.equal(result.japanRows, 2);
  assert.equal(result.longestLived[0], "Japan");
  assert.deepEqual(result.languages, ["<2es>", "<2fr>"]);
  assert.equal(result.writeRefused, "403 Security");
  assert.ok(lines.some((line) => line.startsWith("5. Write refused as expected: 403 Security")));
  assert.equal(stub.everyRequestCarriedTheHeaders, true);
  assert.equal(stub.requests.length, 7);
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

test("the sandbox keeps the job id and ignores the create switch", () => {
  const read = settingsFromEnvironment({ DATAOIL_JOB_ID: "my-job", DATAOIL_CREATE_DATABASE: "true" });
  assert.equal(read.settings.sandbox, true);
  assert.equal(read.settings.jobId, "my-job");
  assert.equal(read.settings.createDatabase, false);
});

test("naming any connection variable leaves the sandbox and names the rest", async () => {
  const read = settingsFromEnvironment({ DATAOIL_URL: "https://acme.dataoil.tech" });
  assert.equal(read.settings, undefined);
  assert.equal(read.problems.length, 2);
  assert.ok(read.problems[0].startsWith("DATAOIL_DATABASE"));
  const errors = [];
  assert.equal(await main([], { DATAOIL_TOKEN: "dot_placeholder" }, quiet, (line) => errors.push(line)), 1);
  assert.ok(errors[0].startsWith("DATAOIL_URL"));
});

test("your own tenant runs the write walk-through; the sandbox named explicitly is still read only", () => {
  assert.equal(settingsFromEnvironment({ DATAOIL_URL: "https://acme.dataoil.tech", DATAOIL_DATABASE: "sample_notes", DATAOIL_TOKEN: "dot_placeholder" }).settings.sandbox, false);
  assert.equal(settingsFromEnvironment({ DATAOIL_URL: "https://SANDBOX.dataoil.tech/", DATAOIL_DATABASE: "Sandbox", DATAOIL_USERNAME: "suser", DATAOIL_PASSWORD: "placeholder" }).settings.sandbox, true);
});

test("settings take a name and password and the create switch", () => {
  const read = settingsFromEnvironment({ DATAOIL_URL: "https://acme.dataoil.tech", DATAOIL_DATABASE: "sample_notes", DATAOIL_USERNAME: "someone", DATAOIL_PASSWORD: "placeholder", DATAOIL_CREATE_DATABASE: "TRUE" });
  assert.deepEqual(read.problems, []);
  assert.equal(read.settings.createDatabase, true);
  assert.equal(read.settings.token, undefined);
  assert.equal(read.settings.jobId, "sample-javascript-client");
});

test("a token and a name together are refused", () => {
  const read = settingsFromEnvironment({ DATAOIL_URL: "https://acme.dataoil.tech", DATAOIL_DATABASE: "sample_notes", DATAOIL_TOKEN: "dot_placeholder", DATAOIL_USERNAME: "someone" });
  assert.equal(read.settings, undefined);
  assert.equal(read.problems.length, 1);
  assert.match(read.problems[0], /not both/);
});
