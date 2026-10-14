// Serves a WebGL build, opens it in headless Chrome, prints the report the player POSTs to /report, exits with its
// failure count. node Tools/serve-webgl.mjs <build dir> [port]
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import { join, extname } from "node:path";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";

const dir = process.argv[2];
const port = Number(process.argv[3] ?? 58100);
const types = { ".html": "text/html", ".js": "application/javascript", ".wasm": "application/wasm", ".data": "application/octet-stream", ".png": "image/png", ".css": "text/css" };
let chrome;
const server = createServer(async (req, res) => {
  if (req.method === "POST" && req.url === "/report") {
    let body = "";
    for await (const chunk of req) body += chunk;
    res.end("ok");
    process.stdout.write(body);
    const failures = Number(/failures=(\d+)/.exec(body)?.[1] ?? 1);
    chrome?.kill();
    server.close();
    process.exit(failures);
  }
  try {
    const path = join(dir, decodeURIComponent((req.url ?? "/").split("?")[0]) === "/" ? "index.html" : decodeURIComponent(req.url.split("?")[0]));
    const data = await readFile(path);
    res.writeHead(200, { "content-type": types[extname(path)] ?? "application/octet-stream" });
    res.end(data);
  } catch { res.writeHead(404); res.end(); }
});
server.listen(port, "127.0.0.1", () => {
  const profile = mkdtempSync(join(tmpdir(), "rqlplayer-chrome-"));
  chrome = spawn("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    ["--headless=new", "--no-first-run", `--user-data-dir=${profile}`, "--use-angle=swiftshader", "--enable-unsafe-swiftshader", `http://127.0.0.1:${port}/`], { stdio: "ignore" });
});
setTimeout(() => { console.error("timeout: no report"); chrome?.kill(); process.exit(99); }, 180000);
