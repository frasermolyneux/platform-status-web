const STATUS_MAP: Record<string, { className: string; label: string; color: string }> = {
  operational: { className: 'status-operational', label: 'Operational', color: '#2da44e' },
  degraded: { className: 'status-degraded', label: 'Degraded Performance', color: '#d4a72c' },
  outage: { className: 'status-outage', label: 'Major Outage', color: '#cf222e' },
  maintenance: { className: 'status-maintenance', label: 'Under Maintenance', color: '#0969da' },
  unknown: { className: 'status-unknown', label: 'Unknown', color: '#656d76' },
};

export function getStatusInfo(status: string) {
  return STATUS_MAP[status] || STATUS_MAP.unknown;
}

export function getStatusDotHtml(status: string): string {
  const info = getStatusInfo(status);
  return `<span class="status-dot ${info.className}" title="${info.label}"></span>`;
}
