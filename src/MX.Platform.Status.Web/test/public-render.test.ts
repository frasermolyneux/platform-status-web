import assert from 'node:assert/strict';
import { test } from 'node:test';
import { JSDOM } from 'jsdom';
import { affectedComponents, evidenceCards, incidentSeed } from '../src/client/evidence';
import type { StatusResponse } from '../src/client/api';

test('incidents identify affected services on cards and component pages, and link their actual GitHub source', async () => {
  const dom = new JSDOM('<main id="app"></main>', { url: 'https://mxstatus.io/' });
  for (const name of ['window', 'document', 'location', 'history', 'HTMLElement'] as const)
    Object.defineProperty(globalThis, name, { value: dom.window[name], configurable: true });
  const { renderOverview, renderComponent, renderIncident, renderHistory } = await import('../src/client/render');
  const data: StatusResponse = {
    schemaVersion: 1, site: { id: 'mx', displayName: 'MX status' }, generatedAt: new Date().toISOString(),
    summary: { status: 'outage', message: 'Outage: GeoLocation Website.' }, maintenance: [],
    components: [{ id: 'geo', name: 'GeoLocation Website', kind: 'leaf', status: 'outage', uptimeWindowDays: 90, uptimeRatio: 1, history: [], openIncidentIds: [2] }],
    incidents: [{ id: 2, title: 'Email delays', components: ['geo'], severity: 'outage', state: 'identified',
      url: '/incident/2', sourceUrl: 'https://github.com/frasermolyneux/status-pages/issues/42', createdAt: '2026-10-23T08:00:00Z',
      updates: [
        { at: '2026-10-23T08:00:00Z', state: 'investigating', severity: 'degraded', body: 'Investigating email delivery' },
        { at: '2026-10-23T09:00:00Z', state: 'identified', severity: 'outage', body: 'Confirmed impact' },
      ] }],
  };
  for (const render of [renderOverview, renderHistory]) {
    render(data);
    assert.match(document.querySelector('.affected-services')?.textContent || '', /GeoLocation Website/);
    assert.equal(document.querySelector<HTMLAnchorElement>('.affected-services a')?.getAttribute('href'), '/component/geo');
  }
  for (const severity of ['outage', 'degraded', 'maintenance']) {
    data.components[0].status = severity;
    renderComponent(data, 'geo');
    assert.ok(document.querySelector(`.component-detail-status.status-${severity} .status-dot`));
    assert.match(document.querySelector('.incidents-section')?.textContent || '', /Email delays/);
  }
  renderIncident(data, '2');
  assert.equal(document.querySelector<HTMLAnchorElement>('a[href*="/issues/42"]')?.href, data.incidents[0].sourceUrl);
  const entries = document.querySelectorAll('.timeline-entry');
  assert.match(entries[0].textContent || '', /identified.*outage/s);
  assert.match(entries[1].textContent || '', /investigating.*degraded/s);
  data.incidents[0].aliases = [20];
  renderIncident(data, '20');
  assert.match(document.querySelector('#app h2')?.textContent || '', /Email delays/);
  data.incidents[0].sourceUrl = 'javascript:alert(1)';
  renderIncident(data, '2');
  assert.equal(document.querySelector('a[href^="javascript:"]'), null);
  assert.doesNotMatch(document.querySelector('#app')?.textContent || '', /View GitHub issue/);
});

test('services drill down to components and checks without flattening the overview', async () => {
  const dom = new JSDOM('<main id="app"></main>', { url: 'https://status.xtremeidiots.com/' });
  for (const name of ['window', 'document', 'location', 'history', 'HTMLElement'] as const)
    Object.defineProperty(globalThis, name, { value: dom.window[name], configurable: true });
  const { renderOverview, renderComponent } = await import('../src/client/render');
  const historyDays = [{ date: '2026-10-23', status: 'outage', uptime: 1, incidentIds: [2] }];
  const data: StatusResponse = {
    schemaVersion: 1, site: { id: 'xi', displayName: 'XI status' }, generatedAt: new Date().toISOString(),
    summary: { status: 'outage' }, incidents: [], maintenance: [],
    components: [{
      id: 'portal', name: 'XtremeIdiots Portal', kind: 'group', status: 'outage', uptimeWindowDays: 90,
      uptimeRatio: 1, history: historyDays, children: [{
        id: 'geo', name: 'GeoLocation Website', kind: 'leaf', status: 'outage', uptimeWindowDays: 90,
        uptimeRatio: 1, history: historyDays, checks: [
          { id: 'http', name: 'HTTP liveness', kind: 'http', status: 'operational', observedAt: new Date().toISOString() },
          { id: 'queue', name: 'Notification delay', kind: 'telemetry', status: 'degraded', observedAt: null },
        ],
      }],
    }],
  };
  renderOverview(data);
  assert.equal(document.querySelectorAll('.component-row').length, 1);
  assert.equal(document.querySelector('.component-row a')?.getAttribute('href'), '/component/portal');
  renderComponent(data, 'portal');
  assert.equal(document.querySelector('.component-row a')?.getAttribute('href'), '/component/geo');
  assert.match(document.querySelector('.panel')?.textContent || '', /Components.*GeoLocation Website/s);
  assert.ok(document.querySelector('.history-bar.status-outage'));
  renderComponent(data, 'geo');
  assert.ok(document.querySelector('a[href="/component/portal"]'));
  assert.match(document.querySelector('.panel')?.textContent || '', /Checks.*HTTP liveness.*Operational.*Notification delay.*Degraded/s);
  assert.match(document.body.textContent || '', /100.00% check availability/);
});

test('public rendering keeps announcements plain text and incident excerpts readable', async () => {
  const dom = new JSDOM('<main id="app"></main>', { url: 'https://mxstatus.io/' });
  for (const name of ['window', 'document', 'location', 'history', 'HTMLElement'] as const)
    Object.defineProperty(globalThis, name, { value: dom.window[name], configurable: true });
  const { renderOverview, renderIncident, renderHistory } = await import('../src/client/render');
  const data = {
    schemaVersion: 1, site: { id: 'mx', displayName: 'MX status' }, generatedAt: '2026-10-23T08:00:00Z',
    summary: { status: 'operational' }, components: [], maintenance: [],
    banners: [{ id: 1, title: '<script>not executable</script>', message: '<img src=x onerror=alert(1)>', tone: 'warning' as const, updatedAt: '2026-10-23T08:00:00Z' }],
    incidents: [{
      id: 1, title: 'Incident', components: [], severity: 'outage', state: 'investigating', url: '/incident/1',
      createdAt: '2026-10-23T08:00:00Z',
      updates: [{ at: '2026-10-23T08:00:00Z', state: 'investigating', body: 'Service unavailable<br>Next update at 10:00' }],
    }],
  };
  for (const render of [() => renderOverview(data), () => renderIncident(data, '1')]) {
    render();
    assert.equal(document.querySelector('.announcement script'), null);
    assert.equal(document.querySelector('.announcement img'), null);
    assert.match(document.querySelector('.announcement')?.textContent || '', /<script>not executable/);
    assert.match(document.querySelector('.announcement')?.textContent || '', /<img/);
  }
  for (const render of [renderOverview, renderHistory]) {
    render(data);
    assert.equal(document.querySelector('.incident-latest')?.textContent, 'Service unavailable Next update at 10:00');
  }
});

test('evidence summaries retain source state and reject unsafe advisory links', () => {
  const cards = evidenceCards([{
    source: 'azure-public-status', id: 'azure', observedAt: '2026-10-23T08:00:00Z',
    evidence: { items: [{ title: 'Advisory', description: '<p>Investigating an issue.</p>', link: 'javascript:alert(1)' }] },
  }]);
  assert.equal(cards[0].summary, 'Investigating an issue.');
  assert.equal(cards[0].href, undefined);
  assert.equal(cards[0].state, 'published');
});

test('Azure advisory technical details grow with individual items, not repeated copies of the entire feed', () => {
  const items = Array.from({ length: 50 }, (_, index) => ({
    title: `Advisory ${index}`, description: 'An individual service advisory.'.repeat(100),
    link: `https://azure.status.microsoft/advisory/${index}`,
  }));
  const cards = evidenceCards([{
    source: 'azure-public-status', id: 'azure', observedAt: '2026-10-23T08:00:00Z',
    evidence: { items, rawFeed: 'RAW_FEED_NOT_REPEATED_IN_CARDS'.repeat(1000) },
  }]);
  assert.equal(cards.length, 50);
  cards.forEach((card, index) => assert.deepEqual(card.record.evidence, items[index]));
  const technicalDetailsLength = cards.reduce((total, card) => total + JSON.stringify(card.record.evidence, null, 2).length, 0);
  assert.ok(technicalDetailsLength <= JSON.stringify({ items }, null, 2).length);
});

test('probe evidence identifies the failing check and seeds only known affected services without copying private diagnostics', () => {
  const card = evidenceCards([{
    source: 'independent-probe', id: 'xi/forums', observedAt: new Date().toISOString(),
    evidence: {
      site: 'xi', component: 'xi.sitewatch.forums-web', name: 'Community Forums',
      success: false, target: 'https://www.xtremeidiots.com/', observer: 'PRIVATE_OBSERVER',
      httpStatus: 503, durationMilliseconds: 81, error: 'HTTP 503 Service Unavailable',
    },
  }])[0];
  assert.equal(card.state, 'failed');
  assert.equal(card.summary, 'HTTP 503 Service Unavailable');
  assert.ok(card.details.some(detail => detail.label === 'HTTP status' && detail.value === '503'));
  const ids = affectedComponents(card, ['xi.sitewatch.forums-web'], {}, []);
  assert.deepEqual(ids, ['xi.sitewatch.forums-web']);
  assert.deepEqual(affectedComponents(card, ['mx.sitewatch.molyneux-me'], {}, []), []);
  const seed = incidentSeed(card, ids, ['Community Forums']);
  assert.equal(seed.state, 'investigating');
  assert.equal(seed.severity, 'outage');
  assert.match(seed.message, /Community Forums/);
  assert.doesNotMatch(seed.message, /PRIVATE_OBSERVER|503|xtremeidiots.com/);
});

test('Azure metric alerts expose measured conditions rather than requiring raw JSON inspection', () => {
  const card = evidenceCards([{
    source: 'azure-alert', id: 'metric-alert', observedAt: new Date().toISOString(),
    evidence: { data: {
      essentials: { alertRule: 'Queue backlog', monitorCondition: 'Fired', severity: 'Sev2', alertTargetIDs: ['/resources/processor'] },
      alertContext: { condition: { allOf: [{ metricName: 'ActiveMessages', metricValue: 450, operator: 'GreaterThan', threshold: 100, timeAggregation: 'Average' }] } },
      customProperties: { workload: 'portal-server-events' },
    } },
  }])[0];
  assert.ok(card.details.some(detail => detail.label === 'ActiveMessages' && detail.value === '450 GreaterThan 100 (Average)'));
  assert.deepEqual(affectedComponents(card, ['xi.sitewatch.server-events'], { 'portal-server-events': ['xi.sitewatch.server-events'] }, []), ['xi.sitewatch.server-events']);
});

test('telemetry evidence keeps check scope and private diagnostics without copying them into the public draft', () => {
  const record = {
    source: 'check-result', id: 'xi/queue', observedAt: new Date().toISOString(),
    evidence: {
      site: 'xi', component: 'xi.sitewatch.server-events', name: 'Event processing',
      checkId: 'queue', status: 'outage', detail: 'PRIVATE_QUEUE_DIAGNOSTICS',
    },
  };
  const card = evidenceCards([record])[0];
  assert.equal(card.title, 'Event processing');
  assert.equal(card.summary, 'PRIVATE_QUEUE_DIAGNOSTICS');
  assert.equal(card.state, 'outage');
  const ids = affectedComponents(card, ['xi.sitewatch.server-events'], {}, []);
  assert.deepEqual(ids, ['xi.sitewatch.server-events']);
  const seed = incidentSeed(card, ids, ['Server Events']);
  assert.equal(seed.severity, 'outage');
  assert.equal(seed.state, 'investigating');
  assert.doesNotMatch(seed.message, /PRIVATE|queue/);
  const recovered = evidenceCards([{ ...record, evidence: { ...record.evidence, status: 'operational' } }])[0];
  assert.equal(incidentSeed(recovered, ids, ['Server Events']).state, 'monitoring');
});
