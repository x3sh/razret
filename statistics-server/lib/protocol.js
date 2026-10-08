const kinds = new Set(['strategy_test', 'service_test', 'probe_failure', 'error', 'app_download', 'app_install', 'engine_download', 'engine_install']);
const errors = new Set(['invalid_package', 'network', 'permission', 'timeout', 'io', 'operation', 'unexpected', 'engine_exit']);
const version = /^\d{1,4}\.\d{1,4}\.\d{1,4}$/;
const strategy = /^general(?: \([A-Za-z0-9 _+.,-]{1,100}\))?\.bat$/;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const scopes = new Set(['strategy_launch','strategy_test','diagnostic','app_download','app_install','engine_download','engine_install','general']);
const cause = /^(?:timeout|response:invalid|gateway:no_hello|websocket:failed|read:failed|engine:-?\d{1,10}|http:[1-5]\d{2}|socket:\d{1,6}|request:\d{1,2}|win32:\d{1,10}|hresult:[A-F0-9]{8})$/;
const allowedReport = new Set(['Schema', 'ReportId', 'Day', 'AppVersion', 'EngineVersion', 'Metrics', 'TesterId']);
const allowedMetric = new Set(['Kind', 'Outcome', 'Strategy', 'Service', 'Error', 'TargetVersion', 'Count', 'Scope', 'Cause']);
function object(value, keys) { return value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).every(k => keys.has(k)); }
export function validateReport(input, now = new Date()) {
  if (!object(input, allowedReport) || ![1,2].includes(input.Schema) || !uuid.test(input.ReportId ?? '') || !version.test(input.AppVersion ?? '') || !version.test(input.EngineVersion ?? '')) throw new Error('invalid_report');
  if (input.Schema === 1 ? input.TesterId != null : !uuid.test(input.TesterId ?? '')) throw new Error('invalid_tester');
  const day = input.Day;
  if (typeof day !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(day) || new Date(day + 'T00:00:00Z').toISOString().slice(0, 10) !== day || day > now.toISOString().slice(0, 10) || day < new Date(now.getTime() - 30 * 86400000).toISOString().slice(0, 10)) throw new Error('invalid_day');
  if (!Array.isArray(input.Metrics) || input.Metrics.length < 1 || input.Metrics.length > 100) throw new Error('invalid_metrics');
  const metrics = input.Metrics.map(m => {
    if (!object(m, allowedMetric) || !kinds.has(m.Kind) || !['success', 'failure'].includes(m.Outcome) || !Number.isInteger(m.Count) || m.Count < 1 || m.Count > 10000) throw new Error('invalid_metric');
    if (m.Strategy != null && (typeof m.Strategy !== 'string' || !strategy.test(m.Strategy))) throw new Error('invalid_strategy');
    if (m.Service != null && !['discord', 'youtube', 'launch'].includes(m.Service)) throw new Error('invalid_service');
    if (m.Error != null && !errors.has(m.Error)) throw new Error('invalid_error');
    if (m.Scope != null && !scopes.has(m.Scope)) throw new Error('invalid_scope');
    if (m.Cause != null && (typeof m.Cause !== 'string' || !cause.test(m.Cause))) throw new Error('invalid_cause');
    if (m.TargetVersion != null && (typeof m.TargetVersion !== 'string' || !version.test(m.TargetVersion))) throw new Error('invalid_version');
    if (['strategy_test', 'service_test', 'probe_failure'].includes(m.Kind) && !m.Strategy) throw new Error('missing_strategy');
    if (['service_test','probe_failure'].includes(m.Kind) && !m.Service) throw new Error('missing_service');
    if (m.Kind === 'probe_failure' && m.Outcome !== 'failure') throw new Error('invalid_probe_failure');
    if (m.Kind === 'error' && (m.Outcome !== 'failure' || !m.Error)) throw new Error('missing_error');
    if (m.Kind.endsWith('_download') || m.Kind.endsWith('_install')) { if (!m.TargetVersion) throw new Error('missing_version'); }
    return { Kind: m.Kind, Outcome: m.Outcome, Strategy: m.Strategy ?? null, Service: m.Service ?? null, Error: m.Error ?? null, TargetVersion: m.TargetVersion ?? null, Count: m.Count, Scope: m.Scope ?? null, Cause: m.Cause ?? null };
  });
  return { Schema: input.Schema, ReportId: input.ReportId.toLowerCase(), Day: day, AppVersion: input.AppVersion, EngineVersion: input.EngineVersion, Metrics: metrics, TesterId: input.TesterId?.toLowerCase() ?? null };
}
export function validateLabel(input) {
  if (!object(input,new Set(['TesterId','Name'])) || !uuid.test(input.TesterId ?? '') || typeof input.Name !== 'string' || input.Name.length > 60 || /[\x00-\x1f\x7f]/.test(input.Name)) throw new Error('invalid_label');
  return { TesterId:input.TesterId.toLowerCase(), Name:input.Name.trim() };
}
export function supportCode(id) { return 'RZ-'+id.replaceAll('-','').slice(0,12).toUpperCase(); }
export function summarize(reports, includeTesters = true) {
  const strategies = new Map(), failures = new Map(), updates = new Map(), versions = new Map(), problems = new Map(), testerReports = new Map();
  let tests = 0, passed = 0, errorCount = 0;
  for (const report of reports) {
    if (report.TesterId && includeTesters) { const group=testerReports.get(report.TesterId)??[];group.push(report);testerReports.set(report.TesterId,group); }
    const v = report.AppVersion + ' / ' + report.EngineVersion;
    versions.set(v, (versions.get(v) ?? 0) + 1);
    for (const m of report.Metrics) {
      if (m.Kind === 'strategy_test' || m.Kind === 'service_test') {
        const row = strategies.get(m.Strategy) ?? { name: m.Strategy, tests: 0, passed: 0, discord: { passed: 0, tests: 0 }, youtube: { passed: 0, tests: 0 } };
        if (m.Kind === 'strategy_test') { tests += m.Count; row.tests += m.Count; if (m.Outcome === 'success') { passed += m.Count; row.passed += m.Count; } }
        else if (row[m.Service]) { row[m.Service].tests += m.Count; if (m.Outcome === 'success') row[m.Service].passed += m.Count; }
        strategies.set(m.Strategy, row);
      }
      if (m.Kind === 'error') { errorCount += m.Count; const key = m.Error; failures.set(key, (failures.get(key) ?? 0) + m.Count); }
      const legacyFailure=report.Schema===1 && m.Kind==='service_test' && m.Outcome==='failure';
      if (legacyFailure || m.Kind === 'probe_failure' || m.Kind === 'error' || m.Kind.endsWith('_install') && m.Outcome === 'failure' || m.Kind.endsWith('_download') && m.Outcome === 'failure') {
        const value={kind:m.Kind,category:m.Error??null,cause:m.Cause??null,scope:m.Scope??(legacyFailure?'strategy_test':null),strategy:m.Strategy??null,service:m.Service??null};
        const key=JSON.stringify(value);const row=problems.get(key)??{...value,count:0};row.count+=m.Count;problems.set(key,row);
      }
      if (m.Kind.endsWith('_install') || m.Kind.endsWith('_download')) {
        if(m.Outcome==='failure')errorCount+=m.Count;
        const key = m.Kind + ':' + m.TargetVersion;
        const row = updates.get(key) ?? { kind: m.Kind, version: m.TargetVersion, success: 0, failure: 0 };
        row[m.Outcome] += m.Count; updates.set(key, row);
      }
    }
  }
  return { reports: reports.length, tests, passed, errors: errorCount,
    strategies: [...strategies.values()].sort((a,b) => b.tests - a.tests),
    failures: [...failures].map(([category, count]) => ({ category, count })).sort((a,b)=>b.count-a.count),
    updates: [...updates.values()], versions: [...versions].map(([version, reports]) => ({ version, reports })).sort((a,b)=>b.reports-a.reports),
    problems:[...problems.values()].sort((a,b)=>b.count-a.count),
    ...(includeTesters ? {testers:[...testerReports].map(([id,list])=>({id,code:supportCode(id),...summarize(list,false)})).sort((a,b)=>b.errors-a.errors || (b.tests-b.passed)-(a.tests-a.passed)),anonymousReports:reports.filter(r=>!r.TesterId).length} : {}) };
}
