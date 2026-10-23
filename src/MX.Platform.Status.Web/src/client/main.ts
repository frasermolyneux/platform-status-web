import { fetchStatus } from './api';
import type { StatusResponse } from './api';
import { addRoute, navigate, resolve } from './router';
import { renderOverview, renderComponent, renderIncident, renderHistory } from './render';

async function loadAndRender(renderer: (data: StatusResponse) => void): Promise<void> {
  try {
    const data = await fetchStatus();
    document.title = data.site.displayName;
    const title = document.getElementById('site-title');
    const tagline = document.getElementById('site-tagline');
    if (title) title.textContent = data.site.displayName;
    if (tagline) tagline.textContent = data.site.tagline || '';
    renderer(data);
  } catch (err) {
    const app = document.getElementById('app');
    if (app) app.textContent = 'Unable to load status data. Please try again later.';
  }
}

addRoute('/', () => loadAndRender(renderOverview));
addRoute('/component/:id', ({ id }) => loadAndRender(data => renderComponent(data, id)));
addRoute('/incident/:id', ({ id }) => loadAndRender(data => renderIncident(data, id)));
addRoute('/history', () => loadAndRender(renderHistory));
for (const path of ['/ops', '/ops/incidents', '/ops/banners', '/ops/evidence']) addRoute(path, () => {
  import('./ops').then(module => module.renderOperations(navigate)).catch(() => {
    const app = document.getElementById('app');
    if (app) app.textContent = 'Unable to load operations. Public status remains available.';
  });
});

document.addEventListener('DOMContentLoaded', () => resolve());

// Auto-refresh every 30s
setInterval(() => {
  if (document.visibilityState === 'visible' && !/^\/ops(?:\/|$)/.test(location.pathname)) {
    resolve();
  }
}, 30_000);
