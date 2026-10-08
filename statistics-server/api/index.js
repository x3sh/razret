import { createHash, createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import { validateReport, summarize, validateLabel } from '../lib/protocol.js';
import { saveReport, readReports, purgeExpired, saveLabel, readLabels } from '../lib/storage.js';

const limits = new Map();
const hash = s => createHash('sha256').update(s).digest('hex');
function equals(a, b) { const x = Buffer.from(a ?? ''), y = Buffer.from(b ?? ''); return x.length === y.length && x.length > 0 && timingSafeEqual(x,y); }
function sessionValid(req) {
  const value = (req.headers.cookie ?? '').split(';').map(s=>s.trim()).find(s=>s.startsWith('razret_session='))?.slice(15);
  if (!value || !process.env.SESSION_SECRET) return false;
  const [expires, nonce, sig] = value.split('.');
  return /^\d+$/.test(expires) && Number(expires) > Date.now() && equals(sig, createHmac('sha256',process.env.SESSION_SECRET).update(expires+'.'+nonce).digest('hex'));
}
async function body(req, max = 32768) {
  if (Number(req.headers['content-length'] ?? 0) > max) throw new Error('body_too_large');
  if (req.body != null) { const text = typeof req.body === 'string' ? req.body : JSON.stringify(req.body); if (Buffer.byteLength(text) > max) throw new Error('body_too_large'); return JSON.parse(text); }
  let text = ''; for await (const chunk of req) { text += chunk; if (Buffer.byteLength(text)>max) throw new Error('body_too_large'); } return JSON.parse(text);
}
function rate(req, bucket, max) {
  // Delivery address is used only in a short-lived, process-local abuse limit.
  // Neither the address nor this hash is persisted or returned to the dashboard.
  const minute = Math.floor(Date.now()/60000);
  const key = hash(String(req.headers['x-forwarded-for'] ?? 'local')) + bucket;
  if (limits.size > 5000) limits.clear();
  const entry = limits.get(key); const count = entry?.minute === minute ? entry.count+1 : 1;
  limits.set(key, {minute,count}); return count <= max;
}
function sameOrigin(req) { return req.headers.origin === `https://${req.headers.host}` || process.env.NODE_ENV !== 'production' && req.headers.origin === `http://${req.headers.host}`; }
export default async function handler(req,res) {
  res.setHeader('Cache-Control','no-store'); res.setHeader('X-Content-Type-Options','nosniff');
  res.setHeader('Content-Type','application/json; charset=utf-8');
  const reply=(status,value)=>{res.statusCode=status;res.end(JSON.stringify(value));};
  const route = new URL(req.url,'https://localhost').pathname;
  try {
    if (route === '/api/health' && req.method === 'GET') return reply(200,{ok:true});
    if (route === '/api/login' && req.method === 'POST') {
      if (!sameOrigin(req) || !rate(req,'login',5)) return reply(429,{error:'Попробуйте позже.'});
      const data=await body(req,1000);
      if (!process.env.ADMIN_PASSWORD_HASH || !process.env.SESSION_SECRET) return reply(503,{error:'Панель ещё не настроена.'});
      if (typeof data.password !== 'string' || !equals(hash(data.password),process.env.ADMIN_PASSWORD_HASH)) return reply(401,{error:'Неверный пароль.'});
      const base=(Date.now()+8*3600000)+'.'+randomBytes(16).toString('hex');
      const value=base+'.'+createHmac('sha256',process.env.SESSION_SECRET).update(base).digest('hex');
      res.setHeader('Set-Cookie',`razret_session=${value}; HttpOnly; SameSite=Strict; Path=/; Max-Age=28800${process.env.NODE_ENV==='production'?'; Secure':''}`);
      return reply(200,{ok:true});
    }
    if (route === '/api/logout' && req.method === 'POST') {
      if (!sameOrigin(req)) return reply(403,{error:'Forbidden'});
      res.setHeader('Set-Cookie',`razret_session=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0${process.env.NODE_ENV==='production'?'; Secure':''}`);return reply(200,{ok:true});
    }
    if (route === '/api/collect' && req.method === 'POST') {
      if (!String(req.headers['content-type']??'').startsWith('application/json')) return reply(415,{error:'JSON required'});
      if (!rate(req,'collect',12)) { res.setHeader('Retry-After','60'); return reply(429,{error:'rate_limit'}); }
      let report; try {report=validateReport(await body(req));} catch {return reply(400,{error:'invalid_report'});}
      await saveReport(report);return reply(202,{accepted:true});
    }
    if (route === '/api/summary' && req.method === 'GET') {
      if (!sessionValid(req)) return reply(401,{error:'Требуется вход.'});
      const {reports,truncated}=await readReports(30);const data=summarize(reports);
      const labels=await readLabels(data.testers.map(t=>t.id));data.testers.forEach(t=>t.name=labels[t.id]??'');
      return reply(200,{...data,truncated});
    }
    if (route === '/api/tester-label' && req.method === 'POST') {
      if (!sessionValid(req)) return reply(401,{error:'Требуется вход.'});
      if (!sameOrigin(req)) return reply(403,{error:'Forbidden'});
      let label;try{label=validateLabel(await body(req,1500));}catch{return reply(400,{error:'Некорректное имя или код.'});}
      await saveLabel(label);return reply(200,{ok:true});
    }
    if (route === '/api/retention' && req.method === 'GET') {
      if (!process.env.CRON_SECRET || !equals(req.headers.authorization, 'Bearer '+process.env.CRON_SECRET)) return reply(401,{error:'Unauthorized'});
      await purgeExpired();return reply(200,{ok:true});
    }
    reply(404,{error:'Not found'});
  } catch { console.error('Statistics storage request failed');reply(503,{error:'Статистика временно недоступна. Повторите позже.'}); }
}
