import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { demoSnapshot, demoInsights, demoSettings } from './public/demo.mjs';
const root = path.resolve(fileURLToPath(new URL('./public/', import.meta.url)));
const port = Number(process.env.PORT || 32271);
const types = {'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.mjs':'text/javascript; charset=utf-8','.js':'text/javascript; charset=utf-8'};
const demo = {'/api/demo':demoSnapshot,'/api/demo/insights':demoInsights,'/api/demo/settings':demoSettings};
const server = http.createServer(async (req,res) => {
  try {
    const url = new URL(req.url, `http://127.0.0.1:${port}`);
    if (req.method !== 'GET') { res.writeHead(405); return res.end(); }
    if (demo[url.pathname]) {
      res.writeHead(200, {'Content-Type':'application/json', 'Cache-Control':'no-store'});
      return res.end(JSON.stringify(demo[url.pathname]()));
    }
    // Same routing as the mod: "/" is the control hub, the themes share the fixed-size overlay page.
    const pathname = url.pathname === '/' ? '/index.html' : ['/apex','/orbit'].includes(url.pathname) ? '/overlay.html' : url.pathname;
    const file = path.resolve(root, '.' + decodeURIComponent(pathname));
    if (!file.startsWith(root + path.sep)) { res.writeHead(403); return res.end(); }
    const body = await readFile(file);
    res.writeHead(200, {'Content-Type':types[path.extname(file)] || 'application/octet-stream','Cache-Control':'no-store'});
    res.end(body);
  } catch { res.writeHead(404); res.end('Not found'); }
});
server.listen(port,'127.0.0.1',()=>console.log(`Control hub: http://127.0.0.1:${port}/  ·  overlays: /apex and /orbit`));
