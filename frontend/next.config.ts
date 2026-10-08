import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  /* config options here */
  /** Keep browser calls at /api/* on the Next.js origin during local development. */
  async rewrites() {
    if (process.env.NODE_ENV !== "development") return [];
    const origin = new URL(process.env.DOCUMIND_API_ORIGIN ?? "http://127.0.0.1:5185");
    if (!["http:", "https:"].includes(origin.protocol) || origin.username || origin.password
        || origin.search || origin.hash || origin.pathname !== "/") {
      throw new Error("DOCUMIND_API_ORIGIN must be a plain HTTP(S) backend origin.");
    }
    // The backend address stays server-side; browser cookies belong to the application origin.
    return [{ source: "/api/:path*", destination: origin.origin + "/api/:path*" }];
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
