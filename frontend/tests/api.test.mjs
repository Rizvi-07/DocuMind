import assert from "node:assert/strict";
import { Buffer } from "node:buffer";
import { readFile } from "node:fs/promises";
import test from "node:test";
import ts from "typescript";

// Load the actual TypeScript helper using the project's existing compiler, without a browser or extra test packages.
// This performs transpilation only; npm run build provides the separate TypeScript type check.
const source = await readFile(new URL("../src/lib/api.ts", import.meta.url), "utf8");
const compiled = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
}).outputText;
const { apiFetch } = await import("data:text/javascript;base64," + Buffer.from(compiled).toString("base64"));

// Unsafe paths must fail before any fetch call can send cookies or a CSRF token elsewhere.
for (const path of ["https://other.example.test/api/login", "//other.example.test/api/login", "/auth/login", "/api/\\login", "/api/\rlogin", "/api/\nlogin"]) {
  test("rejects unsafe API path " + JSON.stringify(path), async (context) => {
    const fetch = context.mock.method(globalThis, "fetch", async () => new Response());
    await assert.rejects(apiFetch(path), /relative \/api\/ path/);
    assert.equal(fetch.mock.callCount(), 0);
  });
}

// Safe HTTP methods do not bootstrap CSRF, but still keep browser credentials and caching consistent.
for (const method of ["GET", "HEAD", "OPTIONS"]) {
  test(method + " uses same-origin cookies without a CSRF fetch", async (context) => {
    const response = new Response(null, { status: 200 });
    const fetch = context.mock.method(globalThis, "fetch", async () => response);
    assert.equal(await apiFetch("/api/v1/auth/me", { method: method.toLowerCase() }), response);
    assert.equal(fetch.mock.callCount(), 1);
    const [path, options] = fetch.mock.calls[0].arguments;
    assert.equal(path, "/api/v1/auth/me");
    assert.equal(options.method, method);
    assert.equal(options.credentials, "same-origin");
    assert.equal(options.cache, "no-store");
  });
}

// Every mutation obtains its paired token first, forwards the caller's payload, and overwrites stale tokens.
for (const method of ["POST", "PUT", "PATCH", "DELETE"]) {
  test(method + " bootstraps CSRF before sending the mutation", async (context) => {
    const response = new Response(null, { status: 204 });
    const fetch = context.mock.method(globalThis, "fetch", async (path) => path === "/api/v1/auth/csrf"
      ? Response.json({ requestToken: "synthetic-fresh-token" }) : response);
    const headers = new Headers({ "Content-Type": "application/json", "X-CSRF-TOKEN": "synthetic-stale-token" });
    const body = JSON.stringify({ synthetic: true });
    const signal = new AbortController().signal;
    assert.equal(await apiFetch("/api/v1/auth/logout", {
      method: method.toLowerCase(), headers, body, signal, credentials: "omit", cache: "force-cache",
    }), response);

    assert.equal(fetch.mock.callCount(), 2);
    const [bootstrapPath, bootstrapOptions] = fetch.mock.calls[0].arguments;
    assert.equal(bootstrapPath, "/api/v1/auth/csrf");
    assert.equal(bootstrapOptions.credentials, "same-origin");
    assert.equal(bootstrapOptions.cache, "no-store");
    const [path, options] = fetch.mock.calls[1].arguments;
    assert.equal(path, "/api/v1/auth/logout");
    assert.equal(options.method, method);
    assert.equal(options.headers.get("X-CSRF-TOKEN"), "synthetic-fresh-token");
    assert.equal(options.headers.get("Content-Type"), "application/json");
    assert.equal(options.body, body);
    assert.equal(options.signal, signal);
    assert.equal(options.credentials, "same-origin");
    assert.equal(options.cache, "no-store");
    // Cloning headers prevents the helper from overwriting a caller-owned Headers object.
    assert.equal(headers.get("X-CSRF-TOKEN"), "synthetic-stale-token");
  });
}

// Identity changes invalidate request tokens, so later mutations must obtain a new one instead of reusing a cache.
test("login then logout obtains a fresh token for each identity", async (context) => {
  let tokenNumber = 0;
  const fetch = context.mock.method(globalThis, "fetch", async (path) => path === "/api/v1/auth/csrf"
    ? Response.json({ requestToken: "synthetic-token-" + ++tokenNumber }) : new Response(null, { status: 204 }));
  await apiFetch("/api/v1/auth/login", { method: "POST" });
  await apiFetch("/api/v1/auth/logout", { method: "POST" });
  assert.equal(fetch.mock.callCount(), 4);
  assert.equal(fetch.mock.calls[1].arguments[1].headers.get("X-CSRF-TOKEN"), "synthetic-token-1");
  assert.equal(fetch.mock.calls[3].arguments[1].headers.get("X-CSRF-TOKEN"), "synthetic-token-2");
});

// Failed bootstrap must stop the state-changing request, including rate-limit and server-error responses.
for (const status of [429, 500]) {
  test("CSRF HTTP " + status + " prevents the mutation", async (context) => {
    const fetch = context.mock.method(globalThis, "fetch", async () => new Response(null, { status }));
    await assert.rejects(apiFetch("/api/v1/auth/logout", { method: "POST" }), /prepare a protected API request/);
    assert.equal(fetch.mock.callCount(), 1);
  });
}

// Network failures are surfaced to the UI and must not fall back to an unprotected request.
test("CSRF network failure prevents the mutation", async (context) => {
  const failure = new Error("synthetic network failure");
  const fetch = context.mock.method(globalThis, "fetch", async () => { throw failure; });
  await assert.rejects(apiFetch("/api/v1/auth/logout", { method: "POST" }), error => error === failure);
  assert.equal(fetch.mock.callCount(), 1);
});

// The default request method stays GET when the caller supplies no options.
test("default request returns the backend response unchanged", async (context) => {
  const response = Response.json({ synthetic: true });
  const fetch = context.mock.method(globalThis, "fetch", async () => response);
  assert.equal(await apiFetch("/api/v1/auth/me"), response);
  assert.equal(fetch.mock.calls[0].arguments[1].method, "GET");
});
