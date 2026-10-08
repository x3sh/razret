import http from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import handler from './api/index.js';
const port=Number(process.env.PORT??4178);
http.createServer(async(req,res)=>{
  try {if(req.url.startsWith('/api/'))return await handler(req,res);
    const pathname=new URL(req.url,'http://localhost').pathname;
    const file={'/':'index.html','/dashboard.js':'dashboard.js','/dashboard.css':'dashboard.css'}[pathname];
    if(!file){res.statusCode=404;return res.end('Not found');}
    res.setHeader('Content-Type',file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html; charset=utf-8');
    res.end(await readFile(path.join('public',file)));
  }catch{res.statusCode=500;res.end('Unavailable');}
}).listen(port,'127.0.0.1',()=>console.log('Statistics preview: http://127.0.0.1:'+port));
