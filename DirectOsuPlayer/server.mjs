import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";

const root = process.cwd();
const port = Number(process.env.PORT || 8787);
const types = new Map([
  [".html", "text/html; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".osu", "text/plain; charset=utf-8"],
]);

createServer(async (req, res) => {
  try {
    const url = new URL(req.url ?? "/", `http://${req.headers.host}`);
    const requested = url.pathname === "/" ? "/index.html" : decodeURIComponent(url.pathname);
    const path = normalize(join(root, requested));
    if (!path.startsWith(root)) {
      res.writeHead(403);
      res.end("forbidden");
      return;
    }

    const body = await readFile(path);
    res.writeHead(200, { "content-type": types.get(extname(path)) ?? "application/octet-stream" });
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end("not found");
  }
}).listen(port, "127.0.0.1", () => {
  console.log(`DirectOsuPlayer http://127.0.0.1:${port}/`);
});
