import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID, createHash } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import handler from '../api/index.js';
import { validateReport, summarize, latestConfirmation } from '../lib/protocol.js';
import { saveReport } from '../lib/storage.js';
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
  const legacy=report();legacy.Metrics.push({Kind:'service_test',Outcome:'failure',Strategy:'general.bat',Service:'discord',Count:1});
  const old=summarize([validateReport(legacy)]);assert.equal(old.problems[0].service,'discord');assert.equal(old.problems[0].cause,null);
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
    assert.deepEqual(labeled.testers[0].versionConfirmation.versions,['0.7.3']);
    assert.equal(labeled.testers[0].versionConfirmation.status,'awaiting');
    const historicalDay=new Date(Date.now()-40*86400000),historical={...report(),Schema:2,TesterId:randomUUID(),Day:historicalDay.toISOString().slice(0,10),AppVersion:'0.7.11'};
    await saveReport(validateReport(historical,historicalDay));
    const withHistory=await (await request('summary',{headers:{Cookie:cookie}})).json();
    const inactive=withHistory.testers.find(t=>t.id===historical.TesterId);
    assert.equal(inactive.versionConfirmation.status,'confirmed');assert.equal(inactive.tests,0);
    assert.equal(withHistory.tests,labeled.tests);assert.equal(withHistory.reports,labeled.reports);
  }finally{await new Promise(resolve=>server.close(resolve));await rm(root,{recursive:true,force:true});delete process.env.RAZRET_LOCAL_DATA;}
});

test('version confirmation uses report day, not upload order or historical report counts',()=>{
  const sample=(day,app)=>({...report(),Day:day,AppVersion:app});
  const earlier=sample('2026-10-20','0.7.9'),latest=sample('2026-10-21','0.7.11');
  const result=latestConfirmation([latest,earlier,earlier,earlier]);
  assert.equal(result.day,'2026-10-21');assert.deepEqual(result.versions,['0.7.11']);assert.equal(result.status,'confirmed');
  assert.deepEqual(latestConfirmation([earlier,latest]),result);
  assert.equal(latestConfirmation([sample('2026-10-21','0.7.9')]).status,'awaiting');
  assert.equal(latestConfirmation([sample('2026-10-21','0.7.12')]).status,'confirmed');
  assert.equal(latestConfirmation([sample('2026-10-21','0.7.100')]).status,'confirmed');
  assert.equal(latestConfirmation([sample('2026-10-20','0.7.11'),sample('2026-10-21','0.7.9')]).status,'awaiting');
});
test('same-day versions stay ambiguous and downloads never confirm the target app version',()=>{
  const older={...report(),AppVersion:'0.7.9'},newer={...report(),AppVersion:'0.7.11'};
  const mixed=latestConfirmation([newer,older]);
  assert.equal(mixed.status,'mixed');assert.equal(mixed.targetConfirmed,true);assert.deepEqual(mixed.versions,['0.7.9','0.7.11']);
  assert.deepEqual(latestConfirmation([older,newer]),mixed);
  assert.equal(latestConfirmation([older,{...older}]).status,'awaiting');
  older.Metrics=[{Kind:'app_download',Outcome:'success',TargetVersion:'0.7.11',Count:1}];
  assert.equal(latestConfirmation([older]).status,'awaiting');
  assert.equal(latestConfirmation([]).status,'awaiting');
});
