import assert from 'node:assert/strict';
import { test } from 'node:test';
import { JSDOM } from 'jsdom';
import { evidenceCards } from '../src/client/evidence';

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
