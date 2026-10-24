import { fetchStatus } from './api';
import type { StatusResponse } from './api';
import { addRoute, navigate, resolve } from './router';
import { renderOverview, renderComponent, renderIncident, renderHistory } from './render';

let generation = 0;
let loading = false;
let renderedPath: string | undefined;

async function loadAndRender(renderer: (data: StatusResponse) => void): Promise<void> {
  const requestGeneration = ++generation;
  loading = true;
  try {
    const data = await fetchStatus();
    if (requestGeneration !== generation) return;
    document.title = data.site.displayName;
    const title = document.getElementById('site-title');
    const tagline = document.getElementById('site-tagline');
    if (title) title.textContent = data.site.displayName;
    if (tagline) tagline.textContent = data.site.tagline || '';
    const app = document.getElementById('app');
    const active = renderedPath === location.pathname ? document.activeElement : null;
    const href = app?.contains(active) && active?.tagName === 'A' ? active.getAttribute('href') : null;
    const links = app ? Array.from(app.querySelectorAll('a')) : [];
    const position = links.filter(link => link.getAttribute('href') === href).findIndex(link => link === active);
    renderer(data);
    renderedPath = location.pathname;
    if (app && href && position >= 0)
      Array.from(app.querySelectorAll('a')).filter(link => link.getAttribute('href') === href)[position]?.focus({ preventScroll: true });
  } catch (err) {
    if (requestGeneration !== generation) return;
    const app = document.getElementById('app');
    if (app) app.textContent = 'Unable to load status data. Please try again later.';
  } finally {
    if (requestGeneration === generation) loading = false;
  }
}

addRoute('/', () => loadAndRender(renderOverview));
addRoute('/component/:id', ({ id }) => loadAndRender(data => renderComponent(data, id)));
addRoute('/incident/:id', ({ id }) => loadAndRender(data => renderIncident(data, id)));
addRoute('/history', () => loadAndRender(renderHistory));
for (const path of ['/ops', '/ops/incidents', '/ops/banners', '/ops/evidence', '/ops/traffic']) addRoute(path, () => {
  const requestGeneration = ++generation;
  loading = false;
  return import('./ops').then(module => {
    if (requestGeneration === generation) return module.renderOperations(navigate);
  }).catch(() => {
    if (requestGeneration !== generation) return;
    const app = document.getElementById('app');
    if (app) app.textContent = 'Unable to load operations. Public status remains available.';
  });
});

document.addEventListener('DOMContentLoaded', () => resolve());

function refreshVisiblePage(): void {
  if (document.visibilityState !== 'visible') return;
  if (/^\/ops(?:\/|$)/.test(location.pathname))
    document.getElementById('app')?.dispatchEvent(new Event('status:refresh-workspace'));
  else if (!loading) resolve();
}

setInterval(refreshVisiblePage, 5_000);
document.addEventListener('visibilitychange', refreshVisiblePage);
