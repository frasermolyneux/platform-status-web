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

function announcements(data: StatusResponse): string {
  return (data.banners || []).map(banner => `<aside class="announcement announcement-${escapeHtml(banner.tone)}" aria-label="Site announcement">
    <span class="eyebrow">Notice</span><h2>${escapeHtml(banner.title)}</h2><p>${escapeHtml(banner.message)}</p>
  </aside>`).join('');
}

function excerpt(html: string): string {
  const content = document.createElement('div');
  content.innerHTML = safeHtml(html);
  content.querySelectorAll('br').forEach(lineBreak => lineBreak.replaceWith(' '));
  const text = content.textContent || '';
  return escapeHtml(text.slice(0, 220)) + (text.length > 220 ? '...' : '');
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
    ? '<div class="stale-warning">Live observations are unavailable; incident updates remain visible.</div>'
    : '';

  let html = `
    <div class="page-heading"><h2>Current service status</h2>
    <p class="muted">Current availability, planned maintenance and updates from our team.</p></div>
    ${announcements(data)}
    ${staleWarning}
    <div class="summary-banner ${summaryInfo.className}">
      <span class="summary-icon">${getStatusDotHtml(data.summary.status)}</span>
      <div><span class="eyebrow">Current status</span><p class="summary-text">${data.summary.message ? escapeHtml(data.summary.message) : summaryInfo.label}</p></div>
    </div>
    <div class="updated-at">${new Date(data.generatedAt).getTime() <= 0 ? 'Awaiting first observation' : `Last checked ${formatRelative(data.generatedAt)}`}<span>Automatically refreshed</span></div>
  `;

  // Active incidents
  const activeIncidents = data.incidents.filter(i => i.state !== 'resolved' && i.severity !== 'maintenance');
  if (activeIncidents.length > 0) {
    html += '<section class="incidents-section"><h2>Active Incidents</h2>';
    for (const inc of activeIncidents) {
      html += renderIncidentCard(inc, data);
    }
    html += '</section>';
  }

  html += '<section class="components-section panel"><div class="section-heading"><h2>Service availability</h2><span class="muted">Live observations</span></div>';
  for (const c of data.components) {
    html += renderComponentRow(c);
    for (const child of flattenComponents(c.children || [])) html += renderComponentRow(child);
  }
  html += '</section>';

  // Maintenance
  const upcomingMaintenance = data.maintenance.filter(m => m.state !== 'completed');
  if (upcomingMaintenance.length > 0) {
    html += '<section class="maintenance-section"><h2>Scheduled Maintenance</h2>';
    for (const m of upcomingMaintenance) {
      html += `<div class="maintenance-card">
        <h3><a href="/incident/${escapeHtml(String(m.id))}">${escapeHtml(m.title)}</a></h3>
        <p>${formatDateTime(m.scheduledStart)} — ${formatDateTime(m.scheduledEnd)}</p>
        <span class="badge badge-maintenance">${escapeHtml(m.state)}</span>
        ${affectedServices(m.components, data)}
      </div>`;
    }
    html += '</section>';
  }

  // History bars (90-day)
  if (data.components.length > 0) {
    html += '<section class="history-section panel"><div class="section-heading"><h2>Availability history</h2><a href="/history">View incident history</a></div><p class="muted history-intro">The last 90 days. Grey periods have no observations and are not counted as healthy.</p>';
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
  return `<div class="component-row${c.kind === 'group' ? ' component-group' : ''}">
    <a href="/component/${escapeHtml(c.id)}" data-component-id="${escapeHtml(c.id)}" class="component-name">${escapeHtml(c.name)}</a>
    <span class="component-uptime">${escapeHtml(uptime)}</span>
    <span class="component-status ${info.className}">${getStatusDotHtml(c.status)}${info.label}</span>
  </div>`;
}

function affectedServices(ids: string[], data: StatusResponse): string {
  const components = flattenComponents(data.components).filter(component => ids.includes(component.id));
  const leaves = components.filter(component => component.kind === 'leaf');
  const affected = leaves.length ? leaves : components;
  return `<p class="affected-services"><strong>Affected services:</strong> ${affected.map(component =>
    `<a href="/component/${escapeHtml(component.id)}">${escapeHtml(component.name)}</a>`).join(', ') || 'See incident updates'}</p>`;
}

function issueLink(incident: IncidentDto): string {
  if (!incident.sourceUrl || !/^https:\/\/github\.com\/[^/?#]+\/[^/?#]+\/issues\/[1-9]\d*$/.test(incident.sourceUrl)) return '';
  return `<p><a href="${escapeHtml(incident.sourceUrl)}" target="_blank" rel="noopener noreferrer">View GitHub issue</a></p>`;
}

function renderIncidentCard(inc: IncidentDto, data: StatusResponse): string {
  const info = getStatusInfo(inc.severity);
  return `<div class="incident-card ${info.className}">
    <h3><a href="/incident/${escapeHtml(String(inc.id))}">${escapeHtml(inc.title)}</a></h3>
    <div class="incident-meta">
      <span class="badge badge-${escapeHtml(inc.severity)}">${escapeHtml(inc.severity)}</span>
      <span class="badge badge-state">${escapeHtml(inc.state)}</span>
      <span>${formatRelative(inc.createdAt)}</span>
    </div>
    ${affectedServices(inc.components, data)}
    ${issueLink(inc)}
    ${inc.updates.length > 0
      ? `<p class="incident-latest">${excerpt(inc.updates[inc.updates.length - 1].body)}</p>`
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
  const component = flattenComponents(data.components).find(c => c.id === componentId);
  if (!component) {
    app.innerHTML = safeHtml('<p>Component not found.</p>');
    return;
  }
  const info = getStatusInfo(component.status);
  app.innerHTML = safeHtml(`
    ${announcements(data)}
    <nav class="breadcrumb"><a href="/">← Back to overview</a></nav>
    <h2>${escapeHtml(component.name)}</h2>
    ${component.description ? `<p>${escapeHtml(component.description)}</p>` : ''}
    <div class="component-detail-status ${info.className}">${getStatusDotHtml(component.status)}${info.label}</div>
    ${component.link ? `<p><a href="${escapeHtml(component.link)}" target="_blank" rel="noopener noreferrer">${escapeHtml(component.link)}</a></p>` : ''}
    ${renderHistoryBars(component)}
    <section class="incidents-section"><h3>Incidents affecting this service</h3>
    ${data.incidents.filter(incident => incident.components.includes(component.id))
      .map(incident => renderIncidentCard(incident, data)).join('') || '<p>No published incidents for this service.</p>'}</section>
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

  const maintenance = data.maintenance.find(window => window.id === incident.id);
  let html = `
    ${announcements(data)}
    <nav class="breadcrumb"><a href="/">← Back to overview</a></nav>
    <h2>${escapeHtml(incident.title)}</h2>
    ${maintenance ? `<p>Maintenance: ${formatDateTime(maintenance.scheduledStart)} — ${formatDateTime(maintenance.scheduledEnd)} (${escapeHtml(maintenance.state)})</p>` : ''}
    <div class="incident-meta">
      <span class="badge badge-${escapeHtml(incident.severity)}">${escapeHtml(incident.severity)}</span>
      <span class="badge badge-state">${escapeHtml(incident.state)}</span>
      <span>Opened ${formatDateTime(incident.createdAt)}</span>
      ${incident.resolvedAt ? `<span>Resolved ${formatDateTime(incident.resolvedAt)}</span>` : ''}
    </div>
    ${affectedServices(incident.components, data)}
    ${issueLink(incident)}
    ${!incident.sourceUrl ? '<p class="muted">Published by the status team. This incident has no linked GitHub issue.</p>' : ''}
    <div class="incident-timeline">
  `;
  for (const update of [...incident.updates].reverse()) {
    html += `<div class="timeline-entry">
      <div class="timeline-meta">${formatDateTime(update.at)} — <strong>${escapeHtml(update.state)}</strong>
        ${update.severity ? `<span class="badge badge-${escapeHtml(update.severity)}">${escapeHtml(update.severity)}</span>`
          : '<span class="muted">Impact level was not recorded for this update</span>'}</div>
      <div class="timeline-body">${update.body}</div>
    </div>`;
  }
  html += '</div>';
  app.innerHTML = safeHtml(html);
}

function flattenComponents(components: ComponentDto[]): ComponentDto[] {
  return components.flatMap(component => [component, ...flattenComponents(component.children || [])]);
}

export function renderHistory(data: StatusResponse): void {
  const app = document.getElementById('app');
  if (!app) return;
  const incidents = [...data.incidents].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  app.innerHTML = safeHtml(`
    ${announcements(data)}
    <nav class="breadcrumb"><a href="/">Back to current status</a></nav>
    <h2>Incident and maintenance history</h2>
    <p>Availability is based on observed checks. Grey periods have no data and are not counted as healthy.</p>
    ${data.dataFreshness?.stale ? '<div class="stale-warning">Live collection is unavailable; showing the last publication.</div>' : ''}
    <section>${flattenComponents(data.components).filter(c => c.kind === 'leaf').map(renderHistoryBars).join('')}</section>
    <section>${incidents.length ? incidents.map(incident => renderIncidentCard(incident, data)).join('') : '<p>No published incidents.</p>'}</section>
  `);
}
