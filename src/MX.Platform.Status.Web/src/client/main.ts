import { fetchStatus } from './api';
import type { StatusResponse } from './api';
import { addRoute, resolve } from './router';
import { renderOverview, renderComponent, renderIncident } from './render';

async function loadAndRender(renderer: (data: StatusResponse) => void): Promise<void> {
  try {
    renderer(await fetchStatus());
  } catch (err) {
    const app = document.getElementById('app');
    if (app) app.textContent = 'Unable to load status data. Please try again later.';
  }
}

addRoute('/', () => loadAndRender(renderOverview));
addRoute('/component/:id', ({ id }) => loadAndRender(data => renderComponent(data, id)));
addRoute('/incident/:id', ({ id }) => loadAndRender(data => renderIncident(data, id)));
addRoute('/history', () => loadAndRender(renderOverview));

document.addEventListener('DOMContentLoaded', () => resolve());

// Auto-refresh every 30s
setInterval(() => {
  if (document.visibilityState === 'visible') {
    resolve();
  }
}, 30_000);
