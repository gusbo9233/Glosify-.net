import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";
export default defineConfig({
    root: fileURLToPath(new URL(".", import.meta.url)),
    plugins: [react()],
    server: {
        port: 5178,
        strictPort: true,
        proxy: { "/api": "http://127.0.0.1:5188" },
    },
    build: { outDir: "../server/wwwroot", emptyOutDir: true },
});
