import assert from 'node:assert/strict';
import { mock, test } from 'node:test';
import { JSDOM } from 'jsdom';
import type { StatusResponse } from '../src/client/api';

const dom = new JSDOM('<h1 id="site-title"></h1><main id="app"></main>', { url: 'https://mxstatus.io/' });
for (const name of ['window', 'document', 'location', 'history', 'Event'] as const)
  Object.defineProperty(globalThis, name, { value: dom.window[name], configurable: true });
Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
await new Promise(resolve => setTimeout(resolve, 0));
const routes = new Map<string, (params: Record<string, string>) => unknown>();
const rendered: string[] = [];
let markup: string | undefined;
let refresh!: () => void;
let interval = 0;
const originalInterval = globalThis.setInterval;
mock.method(globalThis, 'setInterval', (callback: () => void, milliseconds: number) => {
  refresh = callback;
  interval = milliseconds;
  const timer = originalInterval(() => {}, milliseconds);
  clearInterval(timer);
  return timer;
});
mock.module('../src/client/router', { namedExports: {
  addRoute: (path: string, handler: (params: Record<string, string>) => unknown) => routes.set(path, handler),
  navigate: () => {},
  resolve: () => rendered.push('refresh'),
} });
mock.module('../src/client/render', { namedExports: Object.fromEntries(
  ['renderOverview', 'renderComponent', 'renderIncident', 'renderHistory'].map(name =>
    [name, (data: StatusResponse) => {
      rendered.push(`${name}:${data.site.displayName}`);
      if (markup !== undefined) document.querySelector('#app')!.innerHTML = markup;
    }])),
});
mock.module('../src/client/ops', { namedExports: { renderOperations: async () => { document.querySelector('#app')!.textContent = 'Private workspace'; } } });
await import('../src/client/main');

function response(name: string): Response {
  return Response.json({ schemaVersion: 1, site: { id: 'mx', displayName: name },
    generatedAt: new Date().toISOString(), summary: { status: 'operational' }, components: [], incidents: [], maintenance: [] });
}

test('an older fetch cannot overwrite a newer incident or component navigation', async () => {
  let finish!: (response: Response) => void;
  globalThis.fetch = async () => new Promise<Response>(resolve => { finish = resolve; });
  const old = routes.get('/')!({});
  globalThis.fetch = async (_path, init) => {
    assert.equal(init?.cache, 'no-store');
    return response('Latest');
  };
  await routes.get('/component/:id')!({ id: 'geo' });
  finish(response('Obsolete'));
  await old;
  assert.deepEqual(rendered, ['renderComponent:Latest']);
});

test('refreshes are prompt, avoid overlapping public fetches and notify private workspaces without rendering public data', async () => {
  rendered.length = 0;
  assert.ok(interval <= 5000);
  refresh();
  assert.deepEqual(rendered, ['refresh']);
  let finish!: (response: Response) => void;
  globalThis.fetch = async () => new Promise<Response>(resolve => { finish = resolve; });
  const pending = routes.get('/incident/:id')!({ id: '2' });
  refresh();
  assert.equal(rendered.length, 1);
  history.replaceState(null, '', '/ops/incidents');
  let workspaceRefreshes = 0;
  document.querySelector('#app')!.addEventListener('status:refresh-workspace', () => workspaceRefreshes++);
  refresh();
  assert.equal(workspaceRefreshes, 1);
  await routes.get('/ops/incidents')!({});
  finish(response('Obsolete public data'));
  await pending;
  assert.equal(document.querySelector('#app')!.textContent, 'Private workspace');
  assert.equal(rendered.length, 1);
});

test('a stalled response body times out and allows automatic refresh to recover', async context => {
  rendered.length = 0;
  history.replaceState(null, '', '/');
  const controller = new AbortController();
  context.mock.method(AbortSignal, 'timeout', (milliseconds: number) => {
    assert.ok(milliseconds > 0 && milliseconds <= 10_000);
    return controller.signal;
  });
  let readingBody = false;
  globalThis.fetch = async (_path, init) => {
    const signal = init?.signal;
    assert.ok(signal);
    const stalled = Response.json({});
    stalled.json = () => new Promise((_resolve, reject) => {
      readingBody = true;
      signal.addEventListener('abort', () => reject(signal.reason), { once: true });
    });
    return stalled;
  };
  const pending = routes.get('/')!({});
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.ok(readingBody);
  controller.abort(new DOMException('Stalled response', 'TimeoutError'));
  await pending;
  assert.match(document.querySelector('#app')?.textContent || '', /Unable to load status data/);
  refresh();
  assert.deepEqual(rendered, ['refresh']);
});

test('public refresh preserves the focused service link after replacing its DOM', async context => {
  history.replaceState(null, '', '/');
  markup = '<a href="/component/geo">GeoLocation Website</a>';
  context.after(() => { markup = undefined; });
  document.querySelector('#app')!.innerHTML = markup;
  const previous = document.querySelector<HTMLAnchorElement>('#app a')!;
  previous.focus();
  globalThis.fetch = async () => response('Updated status');
  await routes.get('/')!({});
  const next = document.querySelector('#app a');
  assert.notEqual(next, previous);
  assert.equal(document.activeElement, next);
});

test('public refresh retains keyboard focus on a detailed history bucket', async context => {
  history.replaceState(null, '', '/component/geo');
  markup = '<button data-history-range="24h" aria-pressed="true">24 hours</button><div tabindex="0" data-history-bucket="geo|24h|2026-10-25T15:00:00Z">Operational</div>';
  context.after(() => { markup = undefined; });
  globalThis.fetch = async () => response('Updated status');
  await routes.get('/component/:id')!({ id: 'geo' });
  const previous = document.querySelector<HTMLElement>('[data-history-bucket]')!;
  previous.focus();
  await routes.get('/component/:id')!({ id: 'geo' });
  assert.notEqual(document.activeElement, previous);
  assert.equal(document.activeElement, document.querySelector('[data-history-bucket]'));
  markup = '<button data-history-range="24h" aria-pressed="true">24 hours</button>';
  await routes.get('/component/:id')!({ id: 'geo' });
  assert.equal(document.activeElement, document.querySelector('[data-history-range]'));
});
