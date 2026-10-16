import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "jsdom",
    environmentOptions: { jsdom: { url: "https://app.test/" } },
    include: ["test/**/*.test.js"],
    setupFiles: ["test/setup.js"],
  },
});
