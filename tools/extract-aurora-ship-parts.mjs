/**
 * Extracts embedded PNG part sprites from aurora_module_database_generator_v5.html
 * into Pulsar4X.Client/Resources/ship-parts/{category}/NN.png
 *
 * Usage: node tools/extract-aurora-ship-parts.mjs [path-to-html]
 */
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, "..");
const htmlPath =
  process.argv[2] ||
  path.join(
    process.env.USERPROFILE || process.env.HOME || "",
    "Downloads",
    "aurora_module_database_generator_v5.html"
  );
const outRoot = path.join(root, "Pulsar4X", "Pulsar4X.Client", "Resources", "ship-parts");

const html = fs.readFileSync(htmlPath, "utf8");
const dbMatch = html.match(/const DB=(\{[\s\S]*?\});/);
if (!dbMatch) {
  console.error("Could not find const DB=... in", htmlPath);
  process.exit(1);
}

const DB = Function(`"use strict"; return (${dbMatch[1]})`)();
const cats = Object.keys(DB);
let total = 0;

for (const cat of cats) {
  const dir = path.join(outRoot, cat);
  fs.mkdirSync(dir, { recursive: true });
  const parts = DB[cat];
  for (let i = 0; i < parts.length; i++) {
    const dataUrl = parts[i];
    const b64 = dataUrl.replace(/^data:image\/png;base64,/, "");
    const buf = Buffer.from(b64, "base64");
    const name = String(i).padStart(2, "0") + ".png";
    fs.writeFileSync(path.join(dir, name), buf);
    total++;
  }
  console.log(`${cat}: ${parts.length} parts`);
}

// Also dump the render JS for reference while porting
const scriptMatch = html.match(/<script>([\s\S]*)<\/script>/);
if (scriptMatch) {
  const js = scriptMatch[1]
    .replace(/const DB=\{[\s\S]*?\};/, "const DB={/* extracted */};")
    .trim();
  fs.writeFileSync(path.join(outRoot, "_aurora_render_reference.js"), js);
}

fs.writeFileSync(
  path.join(outRoot, "README.md"),
  [
    "# Ship part sprites",
    "",
    "Pixel-art ship modules extracted from `aurora_module_database_generator_v5.html`",
    "(Aurora Coherent Ship Generator). Used by Pulsar4X designer ship visuals.",
    "",
    `Extracted ${total} PNGs across: ${cats.join(", ")}.`,
    "",
  ].join("\n")
);

console.log(`Wrote ${total} PNGs to ${outRoot}`);
