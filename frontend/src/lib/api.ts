"use client";

/**
 * Sends browser requests through the same-origin /api proxy. Fetching a fresh CSRF token
 * for each mutation also handles tokens bound to a previous login/logout identity.
 */
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  if (!path.startsWith("/api/") || path.includes("\\") || /[\r\n]/.test(path)) {
    throw new Error("Use a relative /api/ path for browser API requests.");
  }
  const method = (init.method ?? "GET").toUpperCase();
  const headers = new Headers(init.headers);
  if (!["GET", "HEAD", "OPTIONS"].includes(method)) {
    // The HttpOnly CSRF cookie is managed by the browser; JavaScript uses only the request token.
    const tokenResponse = await fetch("/api/v1/auth/csrf", {
      credentials: "same-origin", cache: "no-store",
    });
    if (!tokenResponse.ok) throw new Error("Unable to prepare a protected API request.");
    const { requestToken } = await tokenResponse.json() as { requestToken: string };
    headers.set("X-CSRF-TOKEN", requestToken);
  }
  return fetch(path, { ...init, method, headers, credentials: "same-origin", cache: "no-store" });
}
