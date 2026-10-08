import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID, createHash } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import handler from '../api/index.js';
import { validateReport, summarize } from '../lib/protocol.js';
function report(){return { Schema:1,ReportId:randomUUID(),Day:new Date().toISOString().slice(0,10),AppVersion:'0.7.3',EngineVersion:'1.10.3',Metrics:[{Kind:'strategy_test',Outcome:'success',Strategy:'general.bat',Count:2}] };}
test('protocol rejects identifiers, precise times, raw messages, paths and invalid counts',()=>{
  assert.equal(validateReport(report()).Metrics[0].Count,2);
  for(const key of ['UserId','Ip','Timestamp','Session','Device'])assert.throws(()=>validateReport({...report(),[key]:'private'}));
  for(const key of ['Detail','Message','StackTrace']){const r=report();r.Metrics[0][key]='secret';assert.throws(()=>validateReport(r));}
  for(const name of ['C:\\Users\\Person\\test.bat','../general.bat','user@example.com']){const r=report();r.Metrics[0].Strategy=name;assert.throws(()=>validateReport(r));}
  const r=report();r.Metrics[0].Count=10001;assert.throws(()=>validateReport(r));
});
test('dashboard summarizes outcomes without distinct-user counts',()=>{
  const data=summarize([validateReport(report())]);assert.equal(data.tests,2);assert.equal(data.passed,2);assert.equal(data.strategies[0].tests,2);assert.equal('users' in data,false);
});
test('v2 groups random tester IDs without linking legacy reports or leaking causes',()=>{
  const first={...report(),Schema:2,TesterId:randomUUID()},second={...report(),Schema:2,TesterId:randomUUID()};
  first.Metrics.push({Kind:'error',Outcome:'failure',Error:'network',Scope:'strategy_test',Cause:'socket:10061',Count:1});
  second.Metrics[0].Outcome='failure';
  const data=summarize([validateReport(first),validateReport(second),validateReport(report())]);
  assert.equal(data.testers.length,2);assert.equal(data.anonymousReports,1);
  assert.equal(data.testers.find(t=>t.id===first.TesterId).errors,1);assert.equal(data.testers.find(t=>t.id===second.TesterId).errors,0);
  first.Metrics[1].Cause='private-host.example';assert.throws(()=>validateReport(first));
});
test('collection is idempotent and dashboard requires authenticated session',async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'razret-stats-'));process.env.RAZRET_LOCAL_DATA=root;
  process.env.ADMIN_PASSWORD_HASH=createHash('sha256').update('test-password').digest('hex');process.env.SESSION_SECRET='test-secret-'.repeat(5);
  const server=http.createServer(handler);await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const base='http://127.0.0.1:'+server.address().port;
  const request=(route,options={})=>fetch(base+'/api/'+route,{...options,headers:{'Content-Type':'application/json',Origin:base,...options.headers}});
  try{
    assert.equal((await request('summary')).status,401);
    assert.equal((await request('summary',{headers:{Cookie:'razret_session=9999999999999.fake.forged'}})).status,401);
    const sample=report();for(let i=0;i<2;i++)assert.equal((await request('collect',{method:'POST',body:JSON.stringify(sample)})).status,202);
    assert.equal((await request('collect',{method:'POST',body:JSON.stringify({...sample,UserId:'secret'})})).status,400);
    assert.equal((await request('login',{method:'POST',body:JSON.stringify({password:'wrong'})})).status,401);
    const login=await request('login',{method:'POST',body:JSON.stringify({password:'test-password'})});assert.equal(login.status,200);const cookie=login.headers.get('set-cookie').split(';')[0];
    const summary=await request('summary',{headers:{Cookie:cookie}});assert.equal(summary.status,200);const data=await summary.json();assert.equal(data.reports,1);assert.equal(data.tests,2);
    assert.equal((await request('logout',{method:'POST',headers:{Cookie:cookie}})).status,200);
    assert.equal((await request('retention')).status,401);
    const tester={...report(),Schema:2,TesterId:randomUUID()};
    assert.equal((await request('collect',{method:'POST',body:JSON.stringify(tester)})).status,202);
    assert.equal((await request('tester-label',{method:'POST',body:JSON.stringify({TesterId:tester.TesterId,Name:'QA tester'})})).status,401);
    assert.equal((await request('tester-label',{method:'POST',headers:{Cookie:cookie},body:JSON.stringify({TesterId:tester.TesterId,Name:'QA tester'})})).status,200);
    const labeled=await (await request('summary',{headers:{Cookie:cookie}})).json();assert.equal(labeled.testers[0].name,'QA tester');
  }finally{await new Promise(resolve=>server.close(resolve));await rm(root,{recursive:true,force:true});delete process.env.RAZRET_LOCAL_DATA;}
});
