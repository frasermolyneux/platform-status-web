import type { StatusResponse, ComponentDto, IncidentDto } from './api';
import { getStatusInfo, getStatusDotHtml } from './status-colors';
import { formatRelative, formatDate, formatDateTime } from './time';
import { navigate } from './router';
import DOMPurify from 'dompurify';

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

/** Sanitize assembled HTML before injection into the DOM. */
function safeHtml(html: string): string {
  return DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
}

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
      <span class="summary-text">${data.summary.message ? escapeHtml(data.summary.message) : summaryInfo.label}</span>
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
        <h3>${escapeHtml(m.title)}</h3>
        <p>${formatDateTime(m.scheduledStart)} — ${formatDateTime(m.scheduledEnd)}</p>
        <span class="badge badge-maintenance">${escapeHtml(m.state)}</span>
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

  app.innerHTML = safeHtml(html);

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
    <a href="/component/${escapeHtml(c.id)}" data-component-id="${escapeHtml(c.id)}" class="component-name">${escapeHtml(c.name)}</a>
    <span class="component-uptime">${escapeHtml(uptime)}</span>
    <span class="component-status ${info.className}">${info.label}</span>
  </div>`;
}

function renderIncidentCard(inc: IncidentDto): string {
  const info = getStatusInfo(inc.severity);
  return `<div class="incident-card ${info.className}">
    <h3><a href="/incident/${escapeHtml(String(inc.id))}">${escapeHtml(inc.title)}</a></h3>
    <div class="incident-meta">
      <span class="badge badge-${escapeHtml(inc.severity)}">${escapeHtml(inc.severity)}</span>
      <span class="badge badge-state">${escapeHtml(inc.state)}</span>
      <span>${formatRelative(inc.createdAt)}</span>
    </div>
    ${inc.updates.length > 0
      // Server-rendered incident HTML is trusted and intentionally rendered without escaping.
      ? `<p class="incident-latest">${inc.updates[inc.updates.length - 1].body.slice(0, 200)}</p>`
      : ''}
  </div>`;
}

function renderHistoryBars(c: ComponentDto): string {
  const bars = c.history.map(d => {
    const info = getStatusInfo(d.status);
    const uptime = d.uptime != null ? ` (${(d.uptime * 100).toFixed(1)}%)` : '';
    return `<div class="history-bar ${info.className}" title="${escapeHtml(d.date)}: ${info.label}${escapeHtml(uptime)}"></div>`;
  }).join('');
  const uptime = c.uptimeRatio !== null ? `${(c.uptimeRatio * 100).toFixed(2)}% uptime` : '';
  return `<div class="history-component">
    <div class="history-label">${escapeHtml(c.name)} <span class="history-uptime">${escapeHtml(uptime)}</span></div>
    <div class="history-bars">${bars}</div>
    <div class="history-range"><span>${c.history.length > 0 ? formatDate(c.history[0].date) : ''}</span><span>${c.history.length > 0 ? formatDate(c.history[c.history.length - 1].date) : ''}</span></div>
  </div>`;
}

export function renderComponent(data: StatusResponse, componentId: string): void {
  const app = document.getElementById('app');
  if (!app) return;
  const component = data.components.find(c => c.id === componentId);
  if (!component) {
    app.innerHTML = safeHtml('<p>Component not found.</p>');
    return;
  }
  const info = getStatusInfo(component.status);
  app.innerHTML = safeHtml(`
    <nav class="breadcrumb"><a href="/">← Back to overview</a></nav>
    <h2>${escapeHtml(component.name)}</h2>
    ${component.description ? `<p>${escapeHtml(component.description)}</p>` : ''}
    <div class="component-detail-status ${info.className}">${info.label}</div>
    ${component.link ? `<p><a href="${escapeHtml(component.link)}" target="_blank" rel="noopener noreferrer">${escapeHtml(component.link)}</a></p>` : ''}
    ${renderHistoryBars(component)}
  `);
}

export function renderIncident(data: StatusResponse, incidentId: string): void {
  const app = document.getElementById('app');
  if (!app) return;
  const incident = data.incidents.find(i => i.id === parseInt(incidentId));
  if (!incident) {
    app.innerHTML = safeHtml('<p>Incident not found.</p>');
    return;
  }
  const info = getStatusInfo(incident.severity);
  let html = `
    <nav class="breadcrumb"><a href="/">← Back to overview</a></nav>
    <h2>${escapeHtml(incident.title)}</h2>
    <div class="incident-meta">
      <span class="badge badge-${escapeHtml(incident.severity)}">${escapeHtml(incident.severity)}</span>
      <span class="badge badge-state">${escapeHtml(incident.state)}</span>
      <span>Opened ${formatDateTime(incident.createdAt)}</span>
      ${incident.resolvedAt ? `<span>Resolved ${formatDateTime(incident.resolvedAt)}</span>` : ''}
    </div>
    <div class="incident-timeline">
  `;
  for (const update of incident.updates) {
    html += `<div class="timeline-entry">
      <div class="timeline-meta">${formatDateTime(update.at)} — <strong>${escapeHtml(update.state)}</strong></div>
      <!-- Server-rendered incident HTML is trusted and intentionally rendered without escaping. -->
      <div class="timeline-body">${update.body}</div>
    </div>`;
  }
  html += '</div>';
  app.innerHTML = safeHtml(html);
}
