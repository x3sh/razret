const kinds = new Set(['strategy_test', 'service_test', 'error', 'app_download', 'app_install', 'engine_download', 'engine_install']);
const errors = new Set(['invalid_package', 'network', 'permission', 'timeout', 'io', 'operation', 'unexpected', 'engine_exit']);
const version = /^\d{1,4}\.\d{1,4}\.\d{1,4}$/;
const strategy = /^general(?: \([A-Za-z0-9 _+.,-]{1,100}\))?\.bat$/;
const allowedReport = new Set(['Schema', 'ReportId', 'Day', 'AppVersion', 'EngineVersion', 'Metrics']);
const allowedMetric = new Set(['Kind', 'Outcome', 'Strategy', 'Service', 'Error', 'TargetVersion', 'Count']);
function object(value, keys) { return value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).every(k => keys.has(k)); }
export function validateReport(input, now = new Date()) {
  if (!object(input, allowedReport) || input.Schema !== 1 || !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(input.ReportId ?? '') || !version.test(input.AppVersion ?? '') || !version.test(input.EngineVersion ?? '')) throw new Error('invalid_report');
  const day = input.Day;
  if (typeof day !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(day) || new Date(day + 'T00:00:00Z').toISOString().slice(0, 10) !== day || day > now.toISOString().slice(0, 10) || day < new Date(now.getTime() - 30 * 86400000).toISOString().slice(0, 10)) throw new Error('invalid_day');
  if (!Array.isArray(input.Metrics) || input.Metrics.length < 1 || input.Metrics.length > 100) throw new Error('invalid_metrics');
  const metrics = input.Metrics.map(m => {
    if (!object(m, allowedMetric) || !kinds.has(m.Kind) || !['success', 'failure'].includes(m.Outcome) || !Number.isInteger(m.Count) || m.Count < 1 || m.Count > 10000) throw new Error('invalid_metric');
    if (m.Strategy != null && (typeof m.Strategy !== 'string' || !strategy.test(m.Strategy))) throw new Error('invalid_strategy');
    if (m.Service != null && !['discord', 'youtube', 'launch'].includes(m.Service)) throw new Error('invalid_service');
    if (m.Error != null && !errors.has(m.Error)) throw new Error('invalid_error');
    if (m.TargetVersion != null && (typeof m.TargetVersion !== 'string' || !version.test(m.TargetVersion))) throw new Error('invalid_version');
    if (['strategy_test', 'service_test'].includes(m.Kind) && !m.Strategy) throw new Error('missing_strategy');
    if (m.Kind === 'service_test' && !m.Service) throw new Error('missing_service');
    if (m.Kind === 'error' && (m.Outcome !== 'failure' || !m.Error)) throw new Error('missing_error');
    if (m.Kind.endsWith('_download') || m.Kind.endsWith('_install')) { if (!m.TargetVersion) throw new Error('missing_version'); }
    return { Kind: m.Kind, Outcome: m.Outcome, Strategy: m.Strategy ?? null, Service: m.Service ?? null, Error: m.Error ?? null, TargetVersion: m.TargetVersion ?? null, Count: m.Count };
  });
  return { Schema: 1, ReportId: input.ReportId.toLowerCase(), Day: day, AppVersion: input.AppVersion, EngineVersion: input.EngineVersion, Metrics: metrics };
}
export function summarize(reports) {
  const strategies = new Map(), failures = new Map(), updates = new Map(), versions = new Map();
  let tests = 0, passed = 0, errorCount = 0;
  for (const report of reports) {
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
      if (m.Kind.endsWith('_install') || m.Kind.endsWith('_download')) {
        const key = m.Kind + ':' + m.TargetVersion;
        const row = updates.get(key) ?? { kind: m.Kind, version: m.TargetVersion, success: 0, failure: 0 };
        row[m.Outcome] += m.Count; updates.set(key, row);
      }
    }
  }
  return { reports: reports.length, tests, passed, errors: errorCount,
    strategies: [...strategies.values()].sort((a,b) => b.tests - a.tests),
    failures: [...failures].map(([category, count]) => ({ category, count })).sort((a,b)=>b.count-a.count),
    updates: [...updates.values()], versions: [...versions].map(([version, reports]) => ({ version, reports })).sort((a,b)=>b.reports-a.reports) };
}
