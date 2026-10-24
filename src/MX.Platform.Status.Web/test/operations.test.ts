import assert from 'node:assert/strict';
import { beforeEach, mock, test } from 'node:test';
import { JSDOM } from 'jsdom';

let signedIn = true;
let needsInteraction = false;
let rejectSave = false;
let loginCount = 0;
let existingBanners: unknown[] = [];
let existingHealth: unknown[] = [];
let existingIncidents: unknown[] = [];
let saveGate: Promise<void> | undefined;
let writerWritable = true;
let trafficReadGate: Promise<void> | undefined;
let trafficReadStarted = 0;
let rejectTrafficRead = false;
let incidentReadGate: Promise<void> | undefined;
let incidentReads = 0;
function trafficRow(mode = 'normal', revision = 0,
  lastOperation: { actor: string; reason: string; updatedAt: string; phase: string; mode: string } | null = null) {
  return { hostname: 'bishopsbees.co.uk', mode, revision, state: mode === 'normal' ? 'normal' : 'active',
    verified: true, routeId: mode === 'normal' ? null : 'route-1',
    routeFingerprint: mode === 'normal' ? null : 'observed-route-fingerprint', statusUrl: 'https://mxstatus.io/',
    httpStatus: null, error: null, lastOperation };
}
let trafficRows = [trafficRow()];
const account = {
  homeAccountId: 'test-account', username: 'operator@example.test', name: 'Test operator',
  idTokenClaims: { roles: ['Status.Operator'] },
};
class InteractionRequiredAuthError extends Error {}
mock.module('@azure/msal-browser', {
  namedExports: {
    InteractionRequiredAuthError,
    PublicClientApplication: class {
      async initialize() {}
      async handleRedirectPromise() { return null; }
      getAllAccounts() { return signedIn ? [account] : []; }
      getActiveAccount() { return signedIn ? account : null; }
      setActiveAccount() {}
      async acquireTokenSilent() {
        if (needsInteraction) throw new InteractionRequiredAuthError('Reconnect your session.');
        return { accessToken: 'test-token' };
      }
      async loginRedirect() { loginCount++; }
      async logoutRedirect() {}
    },
  },
});
const { renderOperations } = await import('../src/client/ops');
const writes: { path: string; body: Record<string, unknown> }[] = [];
const timestamp = '2026-10-23T08:00:00Z';

beforeEach(() => {
  const dom = new JSDOM('<div id="site-title"></div><main id="app"></main>', { url: 'https://mxstatus.io/ops/banners' });
  for (const name of ['window', 'document', 'location', 'history', 'sessionStorage', 'Event', 'HTMLElement', 'HTMLInputElement', 'HTMLSelectElement', 'HTMLTextAreaElement', 'HTMLButtonElement'] as const)
    Object.defineProperty(globalThis, name, { value: dom.window[name], configurable: true, writable: true });
  signedIn = true;
  needsInteraction = rejectSave = false;
  account.idTokenClaims.roles = ['Status.Operator'];
  writes.length = loginCount = 0;
  existingBanners = [];
  existingHealth = [];
  existingIncidents = [{
    id: 1, siteId: 'mx', revision: 1, components: ['mx.sitewatch.molyneux-me'],
    title: 'GitHub managed outage', state: 'investigating', severity: 'outage', published: true,
    sourceUrl: 'https://github.com/frasermolyneux/status-pages/issues/1',
  }];
  saveGate = undefined;
  writerWritable = true;
  trafficRows = [trafficRow()];
  trafficReadGate = undefined;
  trafficReadStarted = 0;
  rejectTrafficRead = false;
  incidentReadGate = undefined;
  incidentReads = 0;
  globalThis.fetch = async (input, init) => {
    const path = String(input);
    if (init?.method === 'POST' || init?.method === 'PUT') {
      writes.push({ path, body: JSON.parse(String(init.body)) });
      await saveGate;
      if (path.startsWith('/api/ops/traffic/')) {
        if (rejectSave) return Response.json({ error: 'Cloudflare did not confirm the change. Refresh to inspect the live route.' }, { status: 409 });
        const body = writes.at(-1)!.body;
        const saved = trafficRow(String(body.mode), Number(body.revision) + 1);
        trafficRows = [saved];
        return Response.json(saved);
      }
      return Response.json(rejectSave ? { error: 'Banner changed. Reload before editing.' } : { id: 2, revision: 1 }, { status: rejectSave ? 409 : 201 });
    }
    if (path === '/api/ops/traffic') {
      const hosts = structuredClone(trafficRows);
      const reject = rejectTrafficRead;
      trafficReadStarted++;
      await trafficReadGate;
      if (reject) throw new Error('Stale refresh failure');
      return Response.json({ enabled: true, hosts,
        quota: { requests: 85000, freeDailyAllowance: 100000, warning: true, observedAt: timestamp, error: null } });
    }
    if (path === '/api/ops/incidents') {
      incidentReads++;
      await incidentReadGate;
    }
    if (path === '/api/ops/incidents/1/github') return Response.json({
      operationId: 'queued-operation', revision: 3, remoteVersion: 'reviewed-version',
      localMessage: 'Approved local update', remote: { number: 1, title: 'Remote correction',
        body: '<script>REMOTE_TEXT_ONLY</script>', labels: ['status:incident'], updatedAt: timestamp, comments: [] },
    });
    const fixtures: Record<string, unknown> = {
      '/api/auth/config': { enabled: true, clientId: 'client', tenantId: 'tenant', scope: 'Status.Access' },
      '/api/status': {
        site: { id: 'mx', displayName: 'MX status' }, summary: { status: 'operational' },
        components: [{ id: 'mx.sitewatch.molyneux-me', name: 'Website', kind: 'leaf', children: [] }],
      },
      '/api/ops/runtime': { publisher: true, writable: writerWritable, github: { enabled: true, repository: 'frasermolyneux/status-pages', pollSeconds: 300 }, workloads: {} },
      '/api/ops/incidents': existingIncidents,
      '/api/ops/banners': existingBanners,
      '/api/ops/health': existingHealth,
      '/api/ops/evidence': [{
        source: 'azure-alert', id: 'alert-1', observedAt: timestamp,
        evidence: { data: { essentials: { alertRule: 'Database availability', description: 'The database needs attention.', severity: 'Sev1', monitorCondition: 'Fired' } } },
      }, {
        source: 'github-issues', id: 'github', observedAt: timestamp,
        evidence: { repository: 'frasermolyneux/status-pages', status: 'healthy', summary: 'GitHub issues are synchronized.' },
      }],
    };
    assert.ok(path in fixtures, `Unexpected request ${path}`);
    return Response.json(fixtures[path]);
  };
});

test('traffic changes require an additional role and exact-host confirmation, without publishing an announcement', async () => {
  history.replaceState(null, '', '/ops/traffic');
  await renderOperations();
  assert.match(document.body.textContent || '', /separately assigned Status.TrafficOperator/);
  assert.ok(Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'Enable maintenance')?.disabled);
  account.idTokenClaims.roles = ['Status.Operator', 'Status.TrafficOperator'];
  await renderOperations();
  assert.match(document.body.textContent || '', /85,000/);
  assert.ok(document.querySelector('[aria-label="Enable maintenance for bishopsbees.co.uk"]'));
  click('Enable maintenance');
  setValue('reason', 'Private investigation');
  setValue('confirmation', 'www.bishopsbees.co.uk');
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  assert.equal(writes.length, 0);
  setValue('confirmation', 'bishopsbees.co.uk');
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => document.querySelector<HTMLElement>('.editor-panel')!.hidden === true);
  assert.deepEqual(writes[0], { path: '/api/ops/traffic/bishopsbees.co.uk',
    body: { mode: 'page', reason: 'Private investigation', confirmation: 'bishopsbees.co.uk', revision: 0, routeId: null, routeFingerprint: null } });
  assert.equal(writes.length, 1);
  assert.match(document.querySelector('.traffic-state')?.textContent || '', /active \/ page/);
});

test('restoring traffic sends the observed route and revision and freezes confirmation while pending', async () => {
  history.replaceState(null, '', '/ops/traffic');
  account.idTokenClaims.roles = ['Status.Operator', 'Status.TrafficOperator'];
  trafficRows = [trafficRow('page', 7)];
  let complete!: () => void;
  saveGate = new Promise(resolve => { complete = resolve; });
  await renderOperations();
  click('Restore traffic');
  setValue('reason', 'Origin recovery confirmed');
  setValue('confirmation', 'bishopsbees.co.uk');
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => writes.length === 1);
  assert.ok(document.querySelector<HTMLTextAreaElement>('textarea')?.disabled);
  assert.equal(writes[0].body.mode, 'normal');
  assert.equal(writes[0].body.revision, 7);
  assert.equal(writes[0].body.routeId, 'route-1');
  assert.equal(writes[0].body.routeFingerprint, 'observed-route-fingerprint');
  complete();
  await until(() => document.querySelector<HTMLElement>('.editor-panel')!.hidden === true);
  assert.match(document.querySelector('.traffic-state')?.textContent || '', /normal \/ normal/);
});

test('workspace refresh re-enables traffic controls after the audit writer recovers without replacing an editor', async () => {
  history.replaceState(null, '', '/ops/traffic');
  account.idTokenClaims.roles = ['Status.Operator', 'Status.TrafficOperator'];
  writerWritable = false;
  await renderOperations();
  const enable = () => Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'Enable maintenance')!;
  assert.equal(enable().disabled, true);
  writerWritable = true;
  click('Refresh workspace');
  await until(() => enable().disabled === false);
  click('Enable maintenance');
  setValue('reason', 'Retain this draft during refresh');
  const form = document.querySelector('form');
  click('Refresh workspace');
  await until(() => !Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'Refresh workspace')!.disabled);
  assert.equal(document.querySelector('form'), form);
  assert.equal(document.querySelector<HTMLTextAreaElement>('textarea')?.value, 'Retain this draft during refresh');
});

test('an uncertain edge change keeps the form and error visible and never sends a reopening request', async () => {
  history.replaceState(null, '', '/ops/traffic');
  account.idTokenClaims.roles = ['Status.Operator', 'Status.TrafficOperator'];
  rejectSave = true;
  await renderOperations();
  click('Enable maintenance');
  setValue('reason', 'Preserve the block after an ambiguous response');
  setValue('confirmation', 'bishopsbees.co.uk');
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => (document.body.textContent || '').includes('Cloudflare did not confirm'));
  assert.equal(writes.length, 1);
  assert.equal(writes[0].body.mode, 'page');
  assert.equal(document.querySelector<HTMLTextAreaElement>('textarea')?.value, 'Preserve the block after an ambiguous response');
  assert.equal(document.querySelector<HTMLElement>('.editor-panel')!.hidden, false);
  assert.ok(document.querySelector<HTMLButtonElement>('form button[type="submit"]')!.disabled);
  assert.match(document.body.textContent || '', /Reopen this hostname's controls before retrying/);
});

for (const staleFailure of [false, true]) test(`a pre-mutation traffic ${staleFailure ? 'failure' : 'read'} cannot overwrite the confirmed edge result`, async () => {
  history.replaceState(null, '', '/ops/traffic');
  account.idTokenClaims.roles = ['Status.Operator', 'Status.TrafficOperator'];
  await renderOperations();
  click('Enable maintenance');
  setValue('reason', 'Block independently of an older refresh');
  setValue('confirmation', 'bishopsbees.co.uk');
  let complete!: () => void;
  trafficReadGate = new Promise(resolve => { complete = resolve; });
  rejectTrafficRead = staleFailure;
  click('Refresh traffic controls');
  await until(() => trafficReadStarted === 2);
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => (document.querySelector('.traffic-state')?.textContent || '').includes('active / page'));
  complete();
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.match(document.querySelector('.traffic-state')?.textContent || '', /active \/ page/);
  assert.ok(Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'Restore traffic'));
  assert.doesNotMatch(document.body.textContent || '', /Stale refresh failure/);
});

test('an ambiguous mutation exposes reconciliation of only its original requested mode', async () => {
  history.replaceState(null, '', '/ops/traffic');
  account.idTokenClaims.roles = ['Status.Operator', 'Status.TrafficOperator'];
  trafficRows = [{ ...trafficRow('page', 8, {
    actor: 'operator', reason: 'Previous restore timed out', updatedAt: timestamp, phase: 'uncertain', mode: 'normal',
  }), state: 'uncertain' }];
  await renderOperations();
  assert.ok(document.querySelector('[aria-label="Reconcile previous request for bishopsbees.co.uk"]'));
  assert.equal(Array.from(document.querySelectorAll('button')).some(button => button.textContent === 'Restore traffic'), false);
  click('Reconcile previous request');
  assert.equal(document.querySelector<HTMLSelectElement>('[name="mode"]')!.options.length, 1);
  assert.equal(document.querySelector<HTMLSelectElement>('[name="mode"]')!.value, 'normal');
  setValue('reason', 'Observe completion of the earlier restore');
  setValue('confirmation', 'bishopsbees.co.uk');
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => document.querySelector<HTMLElement>('.editor-panel')!.hidden === true);
  assert.equal(writes.length, 1);
  assert.equal(writes[0].body.mode, 'normal');
  assert.match(document.body.textContent || '', /No additional edge mutation was sent/);
});

function click(text: string): void {
  const control = Array.from(document.querySelectorAll('button')).find(button => button.textContent === text);
  assert.ok(control, `Missing button: ${text}`);
  control.click();
}

function setValue(name: string, value: string): void {
  const control = document.querySelector<HTMLInputElement | HTMLTextAreaElement>(`[name="${name}"]`);
  assert.ok(control);
  control.value = value;
  control.dispatchEvent(new window.Event('input', { bubbles: true }));
}

async function until(check: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 100 && !check(); attempt++) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(check(), 'Expected UI update did not occur.');
}

test('operator dashboard renders useful private evidence, filters signals and links source-owned incidents', async () => {
  history.replaceState(null, '', '/ops/evidence');
  await renderOperations();
  assert.match(document.body.textContent || '', /Database availability/);
  assert.match(document.body.textContent || '', /The database needs attention/);
  assert.equal(document.querySelectorAll('.evidence-card').length, 2);
  assert.equal(document.querySelector('details')?.open, false);
  assert.equal(document.querySelector('#operator-incidents'), null);
  const filter = document.querySelector<HTMLSelectElement>('[aria-label="Filter evidence by state"]')!;
  filter.value = 'Fired';
  filter.dispatchEvent(new window.Event('change'));
  assert.equal(document.querySelectorAll('.evidence-card').length, 1);
  assert.match(document.querySelector('.evidence-card')?.textContent || '', /Database availability/);
  history.pushState(null, '', '/ops/incidents');
  await renderOperations();
  assert.equal(document.querySelector<HTMLAnchorElement>('.management-row a')?.href, 'https://github.com/frasermolyneux/status-pages/issues/1');
  assert.equal(document.querySelector('#operator-evidence'), null);
});

test('new incidents default to GitHub and retain one operation across draft restoration and failed submissions', async () => {
  history.replaceState(null, '', '/ops/incidents');
  await renderOperations();
  click('New incident');
  setValue('title', 'Approved incident');
  setValue('message', 'Approved public update');
  setValue('components', 'mx.sitewatch.molyneux-me');
  const operation = document.querySelector<HTMLInputElement>('[name="operationId"]')!.value;
  assert.equal(document.querySelector<HTMLSelectElement>('[name="delivery"]')!.value, 'github');
  await renderOperations();
  click('New incident');
  assert.equal(document.querySelector<HTMLInputElement>('[name="operationId"]')!.value, operation);
  rejectSave = true;
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => document.querySelector<HTMLButtonElement>('form button[type="submit"]')?.disabled === false);
  rejectSave = false;
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => writes.length === 2);
  assert.equal(writes[0].body.delivery, 'github');
  assert.equal(writes[0].body.operationId, operation);
  assert.equal(writes[1].body.operationId, operation);
});

test('local emergency publication does not send a GitHub operation', async () => {
  history.replaceState(null, '', '/ops/incidents');
  await renderOperations();
  click('New incident');
  setValue('title', 'Local incident');
  setValue('message', 'Approved public update');
  setValue('components', 'mx.sitewatch.molyneux-me');
  setValue('delivery', 'local');
  document.querySelector<HTMLInputElement>('[name="published"]')!.checked = true;
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => writes.length === 1);
  assert.equal(writes[0].body.delivery, 'local');
  assert.equal(writes[0].body.published, true);
  assert.equal(writes[0].body.operationId, undefined);
});

for (const deferred of [false, true]) test(`queued incidents retain GitHub delivery after ${deferred ? 'deferred' : 'immediate'} draft restoration`, async () => {
  history.replaceState(null, '', '/ops/incidents');
  existingIncidents = [{
    id: 1, siteId: 'mx', revision: 1, components: ['mx.sitewatch.molyneux-me'], title: 'Queued incident',
    state: 'investigating', severity: 'outage', published: true, gitHub: { state: 'pending', error: 'GitHub is unavailable' },
  }];
  await renderOperations();
  assert.match(document.body.textContent || '', /GitHub delivery: pending.*GitHub is unavailable/);
  const savedOperation = crypto.randomUUID();
  sessionStorage.setItem('status-draft:test-account:mx:incident:1', JSON.stringify({
    revision: deferred ? 0 : 1, values: { delivery: 'local', message: 'Next public update', operationId: savedOperation },
  }));
  click('Edit incident');
  if (deferred) click('Restore older draft');
  const delivery = document.querySelector<HTMLSelectElement>('[name="delivery"]')!;
  assert.equal(delivery.value, 'github');
  assert.equal(delivery.disabled, true);
  const operation = document.querySelector<HTMLInputElement>('[name="operationId"]')!.value;
  if (deferred) assert.notEqual(operation, savedOperation);
  else assert.equal(operation, savedOperation);
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => writes.length === 1);
  assert.equal(writes[0].body.delivery, 'github');
});

test('shared delivery conflicts route to the owning brand instead of a peer review endpoint', async () => {
  history.replaceState(null, '', '/ops/incidents');
  existingIncidents = [{
    id: 1, siteId: 'mx', revision: 3, components: ['mx.sitewatch.molyneux-me'], title: 'Shared incident',
    state: 'investigating', severity: 'outage', published: true,
    sourceUrl: 'https://github.com/frasermolyneux/status-pages/issues/1',
    gitHub: { state: 'conflict', siteId: 'xi', incidentId: 4 },
  }];
  await renderOperations();
  assert.equal(Array.from(document.querySelectorAll('button')).some(button => button.textContent === 'Review GitHub delivery'), false);
  assert.equal(document.querySelector<HTMLAnchorElement>('a[href*="status.xtremeidiots.com/ops/incidents"]')?.href,
    'https://status.xtremeidiots.com/ops/incidents#incident-4');
});

test('GitHub conflict review renders remote content safely and requires operation- and version-pinned confirmation', async () => {
  history.replaceState(null, '', '/ops/incidents');
  existingIncidents = [{
    id: 1, siteId: 'mx', revision: 3, components: ['mx.sitewatch.molyneux-me'], title: 'Conflicting incident',
    state: 'investigating', severity: 'outage', published: true,
    sourceUrl: 'https://github.com/frasermolyneux/status-pages/issues/1', gitHub: { state: 'conflict' },
  }];
  await renderOperations();
  click('Review GitHub delivery');
  await until(() => document.querySelector('[name="confirmed"]') !== null);
  assert.equal(document.querySelector('pre script'), null);
  assert.match(document.body.textContent || '', /REMOTE_TEXT_ONLY/);
  const confirmed = document.querySelector<HTMLInputElement>('[name="confirmed"]')!;
  assert.equal(confirmed.checked, false);
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => document.body.textContent?.includes('explicitly confirm') === true);
  assert.equal(writes.length, 0);
  confirmed.checked = true;
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => writes.length === 1);
  assert.equal(writes[0].path, '/api/ops/incidents/1/github/retry');
  assert.deepEqual(writes[0].body, {
    revision: 3, remoteVersion: 'reviewed-version', operationId: 'queued-operation', confirmed: true,
  });
});

test('banner editor previews untrusted text safely and sends explicit brand-scoped publication', async () => {
  await renderOperations();
  click('New announcement');
  setValue('title', 'Maintenance notice');
  setValue('message', '<img src=x onerror=alert(1)> Plain text notice');
  const published = document.querySelector<HTMLInputElement>('[name="published"]')!;
  published.checked = true;
  published.dispatchEvent(new window.Event('input', { bubbles: true }));
  assert.equal(document.querySelector('.announcement img'), null);
  assert.match(document.querySelector('.announcement')?.textContent || '', /<img/);
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => document.querySelector<HTMLElement>('.editor-panel')!.hidden === true);
  assert.equal(writes[0].path, '/api/ops/banners');
  assert.equal(writes[0].body.published, true);
  assert.equal(writes[0].body.message, '<img src=x onerror=alert(1)> Plain text notice');
  assert.equal(sessionStorage.length, 0);
});

test('notices can be archived without deleting their text and restored only as drafts', async () => {
  existingBanners = [
    { id: 7, revision: 2, title: 'Current notice', message: 'Retain this text', tone: 'warning', published: true },
    { id: 8, revision: 3, title: 'Archived notice', message: 'Older text', tone: 'info', published: false, archived: true },
  ];
  await renderOperations();
  assert.doesNotMatch(document.querySelector('#operator-banners')?.textContent || '', /Archived notice/);
  sessionStorage.setItem('status-draft:test-account:mx:banner:7',
    JSON.stringify({ revision: 2, values: { archived: false, published: true } }));
  click('Archive notice');
  assert.equal(document.querySelector<HTMLInputElement>('[name="archived"]')?.checked, true);
  assert.equal(document.querySelector<HTMLInputElement>('[name="published"]')?.checked, false);
  assert.equal(document.querySelector<HTMLInputElement>('[name="published"]')?.disabled, true);
  assert.match(document.querySelector('form')?.textContent || '', /Show this notice on the public status page/);
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => document.querySelector<HTMLElement>('.editor-panel')!.hidden === true);
  assert.equal(writes[0].body.archived, true);
  assert.equal(writes[0].body.published, false);
  assert.equal(writes[0].body.message, 'Retain this text');
  const filter = document.querySelector<HTMLInputElement>('[name="showArchived"]')!;
  filter.checked = true;
  filter.dispatchEvent(new window.Event('change'));
  assert.match(document.querySelector('#operator-banners')?.textContent || '', /Archived notice/);
  click('Restore notice as draft');
  assert.equal(document.querySelector<HTMLInputElement>('[name="archived"]')?.checked, false);
  assert.equal(document.querySelector<HTMLInputElement>('[name="published"]')?.checked, false);
});

test('incident workspaces refresh automatically without replacing an unsaved editor', async () => {
  history.replaceState(null, '', '/ops/incidents');
  await renderOperations();
  const root = document.querySelector('#app')!;
  writerWritable = false;
  root.dispatchEvent(new window.Event('status:refresh-workspace'));
  await until(() => (document.querySelector('[role="status"]')?.textContent || '').includes('Writer unavailable'));
  writerWritable = true;
  click('Refresh workspace');
  await until(() => !Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'New incident')!.disabled);
  click('New incident');
  setValue('title', 'Keep this unsaved title');
  const editor = document.querySelector('form');
  writerWritable = false;
  root.dispatchEvent(new window.Event('status:refresh-workspace'));
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(document.querySelector('form'), editor);
  assert.equal(document.querySelector<HTMLInputElement>('[name="title"]')?.value, 'Keep this unsaved title');
  assert.ok(Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'New incident')!.disabled === false);
});

for (const section of ['incidents', 'evidence-drafts']) test(`automatic workspace refresh preserves ${section} keyboard focus before and during a pending read`, async () => {
  history.replaceState(null, '', '/ops/incidents');
  sessionStorage.setItem('status-evidence-draft:test-account:mx:focus-draft', JSON.stringify({
    title: 'Evidence draft', message: 'Review this update', context: 'Private context',
    components: ['mx.sitewatch.molyneux-me'], severity: 'degraded', state: 'investigating',
    evidence: { source: 'azure-alert', id: 'alert-1', observedAt: timestamp },
  }));
  await renderOperations();
  const root = document.querySelector('#app')!;
  const selector = section === 'incidents' ? '#operator-incidents a[href*="/issues/1"]' : '#operator-evidence-drafts a';
  const management = document.querySelector<HTMLAnchorElement>(selector)!;
  const refreshButton = Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'Refresh workspace')!;
  management.focus();
  const initialReads = incidentReads;
  root.dispatchEvent(new window.Event('status:refresh-workspace'));
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(incidentReads, initialReads);
  assert.equal(document.activeElement, management);
  management.blur();
  let finish!: () => void;
  incidentReadGate = new Promise(resolve => { finish = resolve; });
  writerWritable = false;
  root.dispatchEvent(new window.Event('status:refresh-workspace'));
  await until(() => incidentReads > initialReads);
  management.focus();
  finish();
  await until(() => !refreshButton.disabled);
  assert.equal(document.activeElement, management);
  assert.ok(management.isConnected);
  management.blur();
  root.dispatchEvent(new window.Event('status:refresh-workspace'));
  await until(() => Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'New incident')!.disabled);
});

test('conflicts and expired sessions preserve the unsaved form and its tab-scoped draft', async () => {
  await renderOperations();
  click('New announcement');
  setValue('title', 'Draft title');
  setValue('message', 'Do not lose this update.');
  rejectSave = true;
  const form = document.querySelector('form')!;
  form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => (document.querySelector('[role="status"]')?.textContent || '').includes('Banner changed'));
  assert.equal(document.querySelector('form'), form);
  assert.equal(sessionStorage.length, 1);
  needsInteraction = true;
  form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => Array.from(document.querySelectorAll('button')).some(button => button.textContent === 'Reconnect Microsoft session'));
  assert.equal(document.querySelector<HTMLTextAreaElement>('[name="message"]')?.value, 'Do not lose this update.');
  click('Reconnect Microsoft session');
  assert.equal(loginCount, 1);
});

test('readers get evidence without enabled publishing actions, and signed-out users get a purposeful entry screen', async () => {
  account.idTokenClaims.roles = ['Status.Reader'];
  await renderOperations();
  assert.ok(Array.from(document.querySelectorAll('button')).find(button => button.textContent === 'New announcement')?.disabled);
  history.pushState(null, '', '/ops/evidence');
  await renderOperations();
  assert.match(document.body.textContent || '', /Database availability/);
  assert.ok(Array.from(document.querySelectorAll('button')).filter(button => button.textContent === 'Promote to incident').every(button => button.disabled));
  signedIn = false;
  await renderOperations();
  assert.match(document.querySelector('.login-card')?.textContent || '', /Sign in to operations/);
  assert.equal(document.querySelector('.evidence-card'), null);
});

test('restoring a stale draft requires deliberate review rather than silently replacing a newer banner', async () => {
  existingBanners = [{ id: 7, revision: 2, title: 'Latest saved title', message: 'Latest saved text', tone: 'info', published: true }];
  sessionStorage.setItem('status-draft:test-account:mx:banner:7', JSON.stringify({
    revision: 1, values: { title: 'Older draft title', message: 'Older draft text' },
  }));
  await renderOperations();
  click('Edit banner');
  assert.equal(document.querySelector<HTMLInputElement>('[name="title"]')?.value, 'Latest saved title');
  click('Restore older draft');
  assert.equal(document.querySelector<HTMLInputElement>('[name="title"]')?.value, 'Older draft title');
  assert.match(document.querySelector('.announcement')?.textContent || '', /Older draft text/);
});

test('pending saves freeze submitted fields and never clear or hide a replacement form', async () => {
  let complete!: () => void;
  saveGate = new Promise(resolve => { complete = resolve; });
  await renderOperations();
  click('New announcement');
  setValue('title', 'Submitted title');
  setValue('message', 'Submitted update');
  const submitted = document.querySelector('form')!;
  submitted.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => writes.length === 1);
  assert.ok(submitted.querySelector<HTMLTextAreaElement>('textarea')?.disabled);
  click('New announcement');
  const replacement = document.querySelector('form')!;
  assert.notEqual(replacement, submitted);
  setValue('message', 'A different unsaved update');
  complete();
  await until(() => (document.querySelector('[role="status"]')?.textContent || '').includes('newer draft'));
  assert.equal(document.querySelector('form'), replacement);
  assert.equal(document.querySelector<HTMLElement>('.editor-panel')?.hidden, false);
  assert.equal(document.querySelector<HTMLTextAreaElement>('[name="message"]')?.value, 'A different unsaved update');
  assert.match(sessionStorage.getItem('status-draft:test-account:mx:banner:new') || '', /A different unsaved update/);
});

test('dedicated pages retain independent drafts and evidence promotion requires an editable explicit publication', async () => {
  history.replaceState(null, '', '/ops/evidence');
  let destination = '';
  await renderOperations(path => { destination = path; });
  click('Promote to incident');
  assert.equal(writes.length, 0);
  assert.match(destination, /^\/ops\/incidents\?draft=/);
  history.pushState(null, '', destination);
  await renderOperations();
  assert.equal(document.querySelector<HTMLInputElement>('[name="title"]')?.value, 'Database availability');
  assert.equal(document.querySelector<HTMLInputElement>('[name="published"]')?.checked, false);
  assert.match(document.querySelector('.evidence-context')?.textContent || '', /The database needs attention/);
  assert.doesNotMatch(document.querySelector<HTMLTextAreaElement>('[name="message"]')!.value, /database needs attention/);
  setValue('title', 'Edited public title');
  setValue('message', 'We are investigating intermittent website failures.');
  const service = document.querySelector<HTMLSelectElement>('[name="components"]')!;
  service.options[0].selected = true;
  service.dispatchEvent(new window.Event('input', { bubbles: true }));
  const published = document.querySelector<HTMLInputElement>('[name="published"]')!;
  published.checked = true;
  published.dispatchEvent(new window.Event('input', { bubbles: true }));
  assert.match(document.querySelector('.incident-preview')?.textContent || '', /Edited public title/);
  document.querySelector('form')!.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await until(() => document.querySelector<HTMLElement>('.editor-panel')!.hidden === true);
  assert.equal(writes[0].path, '/api/ops/incidents');
  assert.equal(writes[0].body.title, 'Edited public title');
  assert.equal(writes[0].body.published, true);
  assert.deepEqual(writes[0].body.components, ['mx.sitewatch.molyneux-me']);
  assert.doesNotMatch(String(writes[0].body.message), /alert-1|database needs attention|Source evidence/);
  assert.equal((writes[0].body.evidence as { id: string }).id, 'alert-1');
  assert.equal(location.search, '');
  assert.equal(sessionStorage.length, 0);
});

test('service diagnostics explain incident overrides without blaming a successful check', async () => {
  existingHealth = [{
    id: 'mx.sitewatch.molyneux-me', name: 'Website', status: 'degraded',
    source: 'Independent HTTP probe (publisher)', target: 'https://example.test/',
    expectedIntervalSeconds: 60, matchesPublication: true,
    probe: { success: true, httpStatus: 200, durationMilliseconds: 17, observedAt: new Date().toISOString(), observer: 'publisher', error: null },
    incidents: [{ id: 7, title: 'Intermittent failures', severity: 'degraded', state: 'monitoring' }], maintenance: [],
  }];
  history.replaceState(null, '', '/ops');
  await renderOperations();
  assert.match(document.querySelector('.health-check')?.textContent || '', /Website: degraded/);
  assert.match(document.querySelector('.health-check')?.textContent || '', /HTTP 200/);
  assert.match(document.querySelector('.health-check')?.textContent || '', /Incident #7: Intermittent failures/);
  assert.match(document.querySelector('.health-check')?.textContent || '', /HTTP check passed; the active incident/);
  assert.equal(document.querySelector('#operator-banners'), null);
  assert.equal(document.querySelector('#operator-incidents'), null);
  assert.equal(document.querySelector('.workspace-nav a[aria-current="page"]')?.getAttribute('href'), '/ops');
});

test('promoted drafts can be resumed with their edits after normal cross-page navigation', async () => {
  history.replaceState(null, '', '/ops/evidence');
  let destination = '';
  await renderOperations(path => { destination = path; });
  click('Promote to incident');
  history.pushState(null, '', destination);
  await renderOperations();
  setValue('title', 'Keep this edited incident title');
  setValue('message', 'Keep this edited public update.');
  history.pushState(null, '', '/ops/banners');
  await renderOperations();
  click('New announcement');
  setValue('title', 'Independent banner draft');
  history.pushState(null, '', '/ops/incidents');
  await renderOperations();
  assert.equal(document.querySelector('form'), null);
  const resume = document.querySelector<HTMLAnchorElement>('#operator-evidence-drafts a')!;
  assert.ok(resume);
  assert.match(document.querySelector('#operator-evidence-drafts')?.textContent || '', /Keep this edited incident title/);
  history.pushState(null, '', resume.href);
  await renderOperations();
  assert.equal(document.querySelector<HTMLInputElement>('[name="title"]')?.value, 'Keep this edited incident title');
  assert.equal(document.querySelector<HTMLTextAreaElement>('[name="message"]')?.value, 'Keep this edited public update.');
  assert.match(document.querySelector('.evidence-context')?.textContent || '', /The database needs attention/);
  assert.equal(document.querySelector<HTMLInputElement>('[name="published"]')?.checked, false);
  assert.match(sessionStorage.getItem('status-draft:test-account:mx:banner:new') || '', /Independent banner draft/);
});

test('workspace fragment navigation preserves its editor with router handlers installed', async () => {
  const router = await import('../src/client/router');
  let renders = 0;
  router.addRoute('/ops/banners', () => { renders++; void renderOperations(); });
  router.addRoute('/history', () => { renders++; document.getElementById('app')!.textContent = 'History route'; });
  router.resolve();
  await until(() => document.querySelector('#operator-banners') !== null);
  click('New announcement');
  setValue('title', 'Retain this editor');
  const form = document.querySelector('form');
  history.pushState(null, '', '#preview');
  window.dispatchEvent(new window.PopStateEvent('popstate'));
  assert.equal(renders, 1);
  assert.equal(document.querySelector('form'), form);
  assert.equal(document.querySelector<HTMLInputElement>('[name="title"]')?.value, 'Retain this editor');
  history.pushState(null, '', '/history');
  window.dispatchEvent(new window.PopStateEvent('popstate'));
  assert.equal(renders, 2);
  assert.equal(document.getElementById('app')?.textContent, 'History route');
});
