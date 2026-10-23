import assert from 'node:assert/strict';
import { beforeEach, mock, test } from 'node:test';
import { JSDOM } from 'jsdom';

let signedIn = true;
let needsInteraction = false;
let rejectSave = false;
let loginCount = 0;
let existingBanners: unknown[] = [];
let saveGate: Promise<void> | undefined;
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
  const dom = new JSDOM('<div id="site-title"></div><main id="app"></main>', { url: 'https://mxstatus.io/ops' });
  for (const name of ['window', 'document', 'location', 'history', 'sessionStorage', 'Event', 'HTMLElement', 'HTMLInputElement', 'HTMLSelectElement', 'HTMLTextAreaElement', 'HTMLButtonElement'] as const)
    Object.defineProperty(globalThis, name, { value: dom.window[name], configurable: true, writable: true });
  signedIn = true;
  needsInteraction = rejectSave = false;
  account.idTokenClaims.roles = ['Status.Operator'];
  writes.length = loginCount = 0;
  existingBanners = [];
  saveGate = undefined;
  globalThis.fetch = async (input, init) => {
    const path = String(input);
    if (init?.method === 'POST' || init?.method === 'PUT') {
      writes.push({ path, body: JSON.parse(String(init.body)) });
      await saveGate;
      return Response.json(rejectSave ? { error: 'Banner changed. Reload before editing.' } : { id: 2, revision: 1 }, { status: rejectSave ? 409 : 201 });
    }
    const fixtures: Record<string, unknown> = {
      '/api/auth/config': { enabled: true, clientId: 'client', tenantId: 'tenant', scope: 'Status.Access' },
      '/api/status': {
        site: { id: 'mx', displayName: 'MX status' }, summary: { status: 'operational' },
        components: [{ id: 'mx.sitewatch.molyneux-me', name: 'Website', kind: 'leaf', children: [] }],
      },
      '/api/ops/runtime': { publisher: true, writable: true, github: { enabled: true, repository: 'frasermolyneux/status-pages', pollSeconds: 300 }, workloads: {} },
      '/api/ops/incidents': [{
        id: 1, title: 'GitHub managed outage', state: 'investigating', severity: 'outage', published: true,
        sourceUrl: 'https://github.com/frasermolyneux/status-pages/issues/1',
      }],
      '/api/ops/banners': existingBanners,
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
  await renderOperations();
  assert.match(document.body.textContent || '', /Database availability/);
  assert.match(document.body.textContent || '', /The database needs attention/);
  assert.equal(document.querySelectorAll('.evidence-card').length, 2);
  assert.equal(document.querySelector('details')?.open, false);
  assert.equal(document.querySelector<HTMLAnchorElement>('.management-row a')?.href, 'https://github.com/frasermolyneux/status-pages/issues/1');
  const filter = document.querySelector<HTMLSelectElement>('[aria-label="Filter evidence by state"]')!;
  filter.value = 'Fired';
  filter.dispatchEvent(new window.Event('change'));
  assert.equal(document.querySelectorAll('.evidence-card').length, 1);
  assert.match(document.querySelector('.evidence-card')?.textContent || '', /Database availability/);
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
  assert.match(document.body.textContent || '', /Database availability/);
  signedIn = false;
  await renderOperations();
  assert.match(document.querySelector('.login-card')?.textContent || '', /Your operations, in one place/);
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
