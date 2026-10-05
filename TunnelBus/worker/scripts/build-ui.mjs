// Regenerates src/ui.ts from ui/index.html (the Worker bundles TypeScript only). `node scripts/build-ui.mjs`
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
const root = path.join(path.dirname(fileURLToPath(import.meta.url)), "..");
const html = fs.readFileSync(path.join(root, "ui", "index.html"), "utf8");
fs.writeFileSync(path.join(root, "src", "ui.ts"),
  "// The dashboard: one self-contained page (no build step, no framework, no external requests).\n" +
  "// GENERATED from ui/index.html by scripts/build-ui.mjs; edit that file and re-run.\n" +
  "export const UI_HTML = " + JSON.stringify(html) + ";\n");
