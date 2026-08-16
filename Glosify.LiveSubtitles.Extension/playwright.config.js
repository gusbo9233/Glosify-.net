import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./test-browser",
  timeout: 45_000,
  fullyParallel: false,
  workers: 1,
  reporter: process.env.CI ? "github" : "list",
  use: { trace: "retain-on-failure" },
});
