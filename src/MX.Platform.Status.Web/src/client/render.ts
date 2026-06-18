import type { StatusResponse, ComponentDto, IncidentDto } from './api';
import { getStatusInfo, getStatusDotHtml } from './status-colors';
import { formatRelative, formatDate, formatDateTime } from './time';
import { navigate } from './router';

export function renderOverview(data: StatusResponse): void {
  const app = document.getElementById('app');
  if (!app) return;

  // Update site header
  const titleEl = document.getElementById('site-title');
  const taglineEl = document.getElementById('site-tagline');
  if (titleEl) titleEl.textContent = data.site.displayName;
  if (taglineEl) taglineEl.textContent = data.site.tagline || '';

  const summaryInfo = getStatusInfo(data.summary.status);
  const staleWarning = data.dataFreshness?.stale
    ? '<div class="stale-warning">⚠ Showing cached data — live query unavailable</div>'
    : '';

  let html = `
    ${staleWarning}
    <div class="summary-banner ${summaryInfo.className}">
      <span class="summary-icon">${getStatusDotHtml(data.summary.status)}</span>
      <span class="summary-text">${data.summary.message || summaryInfo.label}</span>
    </div>
    <div class="updated-at">Updated ${formatRelative(data.generatedAt)}</div>
  `;

  // Components
  html += '<section class="components-section"><h2>Components</h2>';
  for (const c of data.components) {
    html += renderComponentRow(c);
  }
  html += '</section>';

  // Active incidents
  const activeIncidents = data.incidents.filter(i => i.state !== 'resolved');
  if (activeIncidents.length > 0) {
    html += '<section class="incidents-section"><h2>Active Incidents</h2>';
    for (const inc of activeIncidents) {
      html += renderIncidentCard(inc);
    }
    html += '</section>';
  }

  // Maintenance
  if (data.maintenance.length > 0) {
    html += '<section class="maintenance-section"><h2>Scheduled Maintenance</h2>';
    for (const m of data.maintenance) {
      html += `<div class="maintenance-card">
        <h3>${m.title}</h3>
        <p>${formatDateTime(m.scheduledStart)} — ${formatDateTime(m.scheduledEnd)}</p>
        <span class="badge badge-maintenance">${m.state}</span>
      </div>`;
    }
    html += '</section>';
  }

  // History bars (90-day)
  if (data.components.length > 0) {
    html += '<section class="history-section"><h2>Uptime</h2>';
    for (const c of data.components) {
      html += renderHistoryBars(c);
    }
    html += '</section>';
  }

  app.innerHTML = html;

  // Bind click handlers for component links
  app.querySelectorAll('[data-component-id]').forEach(el => {
    el.addEventListener('click', (e) => {
      e.preventDefault();
      navigate(`/component/${(el as HTMLElement).dataset.componentId}`);
    });
  });
}

function renderComponentRow(c: ComponentDto): string {
  const info = getStatusInfo(c.status);
  const uptime = c.uptimeRatio !== null ? `${(c.uptimeRatio * 100).toFixed(2)}%` : '—';
  return `<div class="component-row">
    <a href="/component/${c.id}" data-component-id="${c.id}" class="component-name">${c.name}</a>
    <span class="component-uptime">${uptime}</span>
    <span class="component-status ${info.className}">${info.label}</span>
  </div>`;
}

function renderIncidentCard(inc: IncidentDto): string {
  const info = getStatusInfo(inc.severity);
  return `<div class="incident-card ${info.className}">
    <h3><a href="/incident/${inc.id}">${inc.title}</a></h3>
    <div class="incident-meta">
      <span class="badge badge-${inc.severity}">${inc.severity}</span>
      <span class="badge badge-state">${inc.state}</span>
      <span>${formatRelative(inc.createdAt)}</span>
    </div>
    ${inc.updates.length > 0 ? `<p class="incident-latest">${inc.updates[inc.updates.length - 1].body.slice(0, 200)}</p>` : ''}
  </div>`;
}

function renderHistoryBars(c: ComponentDto): string {
  const bars = c.history.map(d => {
    const info = getStatusInfo(d.status);
    return `<div class="history-bar ${info.className}" title="${d.date}: ${info.label}${d.uptime != null ? ` (${(d.uptime * 100).toFixed(1)}%)` : ''}"></div>`;
  }).join('');
  const uptime = c.uptimeRatio !== null ? `${(c.uptimeRatio * 100).toFixed(2)}% uptime` : '';
  return `<div class="history-component">
    <div class="history-label">${c.name} <span class="history-uptime">${uptime}</span></div>
    <div class="history-bars">${bars}</div>
    <div class="history-range"><span>${c.history.length > 0 ? formatDate(c.history[0].date) : ''}</span><span>${c.history.length > 0 ? formatDate(c.history[c.history.length - 1].date) : ''}</span></div>
  </div>`;
}

export function renderComponent(data: StatusResponse, componentId: string): void {
  const app = document.getElementById('app');
  if (!app) return;
  const component = data.components.find(c => c.id === componentId);
  if (!component) {
    app.innerHTML = '<p>Component not found.</p>';
    return;
  }
  const info = getStatusInfo(component.status);
  app.innerHTML = `
    <nav class="breadcrumb"><a href="/">← Back to overview</a></nav>
    <h2>${component.name}</h2>
    ${component.description ? `<p>${component.description}</p>` : ''}
    <div class="component-detail-status ${info.className}">${info.label}</div>
    ${component.link ? `<p><a href="${component.link}" target="_blank">${component.link}</a></p>` : ''}
    ${renderHistoryBars(component)}
  `;
}

export function renderIncident(data: StatusResponse, incidentId: string): void {
  const app = document.getElementById('app');
  if (!app) return;
  const incident = data.incidents.find(i => i.id === parseInt(incidentId));
  if (!incident) {
    app.innerHTML = '<p>Incident not found.</p>';
    return;
  }
  const info = getStatusInfo(incident.severity);
  let html = `
    <nav class="breadcrumb"><a href="/">← Back to overview</a></nav>
    <h2>${incident.title}</h2>
    <div class="incident-meta">
      <span class="badge badge-${incident.severity}">${incident.severity}</span>
      <span class="badge badge-state">${incident.state}</span>
      <span>Opened ${formatDateTime(incident.createdAt)}</span>
      ${incident.resolvedAt ? `<span>Resolved ${formatDateTime(incident.resolvedAt)}</span>` : ''}
    </div>
    <div class="incident-timeline">
  `;
  for (const update of incident.updates) {
    html += `<div class="timeline-entry">
      <div class="timeline-meta">${formatDateTime(update.at)} — <strong>${update.state}</strong></div>
      <div class="timeline-body">${update.body}</div>
    </div>`;
  }
  html += '</div>';
  app.innerHTML = html;
}
