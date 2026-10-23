export interface EvidenceRecord {
  source: string;
  id: string;
  observedAt: string;
  evidence: unknown;
}

export interface EvidenceCard {
  title: string;
  summary: string;
  source: string;
  severity: string;
  state: string;
  observedAt: string;
  href?: string;
  record: EvidenceRecord;
}

function object(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function httpsLink(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : undefined;
  } catch { return undefined; }
}

export function evidenceCards(records: EvidenceRecord[]): EvidenceCard[] {
  return records.flatMap(record => {
    const data = object(record.evidence);
    const base = { source: record.source, observedAt: record.observedAt, record };
    if (record.source === 'azure-alert') {
      const essentials = object(object(data.data).essentials);
      const targets = Array.isArray(essentials.alertTargetIDs)
        ? essentials.alertTargetIDs.filter((value): value is string => typeof value === 'string') : [];
      return [{
        ...base, title: text(essentials.alertRule, 'Azure monitoring alert'),
        summary: text(essentials.description) || targets.map(target => target.split('/').pop()).join(', ') || 'An Azure alert was received.',
        severity: text(essentials.severity, 'unknown'), state: text(essentials.monitorCondition, 'received'),
      }];
    }
    if (record.source === 'azure-public-status') {
      const items = Array.isArray(data.items) ? data.items : [];
      if (items.length === 0)
        return [{ ...base, title: 'Azure public status', summary: 'No active items in the latest Azure status feed.', severity: 'info', state: 'clear' }];
      return items.map(item => {
        const entry = object(item);
        return {
          ...base, title: text(entry.title, 'Azure service advisory'),
          record: { ...record, evidence: entry },
          summary: text(entry.description).replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim(),
          severity: 'advisory', state: 'published', href: httpsLink(entry.link),
        };
      });
    }
    if (record.source === 'github-issues')
      return [{
        ...base, title: 'GitHub incident publishing', summary: text(data.summary, 'No synchronization summary is available.'),
        severity: data.status === 'healthy' ? 'info' : 'warning', state: text(data.status, 'unknown'),
        href: httpsLink(`https://github.com/${text(data.repository)}`),
      }];
    if (record.source === 'sitewatch') {
      const regions = Array.isArray(data.regions) ? data.regions : [];
      return [{
        ...base, title: text(data.component, 'Workload availability observations'),
        summary: regions.length ? `${regions.length} regional observations. Expand the technical details for probe results.` : 'Regional probe details are available below.',
        severity: 'info', state: 'observed',
      }];
    }
    return [{ ...base, title: record.source, summary: 'Restricted integration evidence. Expand the technical details for the complete record.', severity: 'info', state: 'received' }];
  });
}
