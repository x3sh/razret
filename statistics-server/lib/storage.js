import { put, get, list, del, BlobError, BlobNotFoundError } from '@vercel/blob';
import { mkdir, readFile, writeFile, readdir, unlink } from 'node:fs/promises';
import path from 'node:path';

const local = () => process.env.RAZRET_LOCAL_DATA && process.env.NODE_ENV !== 'production';
export async function saveReport(report) {
  const name = `reports/${report.Day}/${report.ReportId}.json`;
  const body = JSON.stringify(report);
  if (local()) {
    const file = path.join(process.env.RAZRET_LOCAL_DATA, name); await mkdir(path.dirname(file), { recursive: true });
    try { await writeFile(file, body, { flag: 'wx' }); } catch (e) { if (e.code !== 'EEXIST') throw e; }
  } else {
    try { await put(name, body, { access: 'private', addRandomSuffix: false, allowOverwrite: false, contentType: 'application/json' }); }
    catch (e) { if (!(e instanceof BlobError) || !e.message.includes('already exists')) throw e; }
  }
}
export async function readReports(days = 30) {
  const cutoff = new Date(Date.now() - days * 86400000).toISOString().slice(0,10);
  const retention = new Date(Date.now() - 90 * 86400000).toISOString().slice(0,10);
  const reports = []; let truncated = false;
  if (local()) {
    const root = path.join(process.env.RAZRET_LOCAL_DATA, 'reports');
    const folders = await readdir(root).catch(e => { if (e.code === 'ENOENT') return []; throw e; });
    for (const day of folders.sort().reverse()) {
      if (day < cutoff) continue;
      for (const file of await readdir(path.join(root,day))) {
        if (reports.length >= 5000) { truncated = true; break; }
        reports.push(JSON.parse(await readFile(path.join(root,day,file), 'utf8')));
      }
    }
  } else {
    let cursor;
    do {
      const page = await list({ prefix: 'reports/', limit: 1000, cursor });
      const expired = page.blobs.filter(b => b.pathname.split('/')[1] < retention);
      if (expired.length) await del(expired.map(b=>b.url));
      const selected = page.blobs.filter(b => b.pathname.split('/')[1] >= cutoff);
      for (let i=0;i<selected.length;i+=10) {
        const loaded = await Promise.all(selected.slice(i,i+10).map(async b=>{
          const result = await get(b.pathname, { access:'private', useCache:false });
          if (result?.statusCode !== 200) throw new Error('report_unavailable');
          return JSON.parse(await new Response(result.stream).text());
        }));
        reports.push(...loaded);
        if (reports.length >= 5000) { truncated = page.hasMore || i+10 < selected.length; break; }
      }
      cursor = page.hasMore ? page.cursor : undefined;
    } while (cursor && reports.length < 5000);
  }
  return { reports, truncated };
}
export async function purgeExpired() {
  const cutoff = new Date(Date.now() - 90 * 86400000).toISOString().slice(0,10);
  if (local()) return;
  let cursor;
  do {
    const page = await list({ prefix:'reports/', limit:1000, cursor });
    const expired = page.blobs.filter(b=>b.pathname.split('/')[1] < cutoff);
    if (expired.length) await del(expired.map(b=>b.url));
    cursor = page.hasMore ? page.cursor : undefined;
  } while (cursor);
  cursor = undefined;
  do {
    const page = await list({prefix:'labels/',limit:1000,cursor});
    const expired=page.blobs.filter(b=>new Date(b.uploadedAt).toISOString().slice(0,10)<cutoff);
    if(expired.length)await del(expired.map(b=>b.url));
    cursor=page.hasMore?page.cursor:undefined;
  }while(cursor);
}
export async function saveLabel(label) {
  const name=`labels/${label.TesterId}.json`;
  if (local()) { const file=path.join(process.env.RAZRET_LOCAL_DATA,name);await mkdir(path.dirname(file),{recursive:true});await writeFile(file,JSON.stringify(label)); }
  else await put(name,JSON.stringify(label),{access:'private',addRandomSuffix:false,allowOverwrite:true,contentType:'application/json'});
}
export async function readLabels(ids) {
  const labels={};
  for(let i=0;i<ids.length;i+=10)await Promise.all(ids.slice(i,i+10).map(async id=>{
    const name=`labels/${id}.json`;let data;
    if(local()) { try{data=JSON.parse(await readFile(path.join(process.env.RAZRET_LOCAL_DATA,name),'utf8'));}catch(e){if(e.code!=='ENOENT')throw e;} }
    else { try { const result=await get(name,{access:'private',useCache:false});if(result?.statusCode===200)data=JSON.parse(await new Response(result.stream).text()); } catch(e) { if(!(e instanceof BlobNotFoundError))throw e; } }
    if(data)labels[id]=data.Name;
  }));
  return labels;
}
