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
  components: string[];
  site?: string;
  workload?: string;
  resources?: string[];
  details: { label: string; value: string }[];
  record: EvidenceRecord;
}

export interface ProbeObservation {
  site: string;
  component: string;
  name: string;
  target: string;
  observer: string;
  observedAt: string;
  success: boolean;
  httpStatus: number | null;
  durationMilliseconds: number;
  error: string | null;
}

export interface ComponentHealth {
  id: string;
  name: string;
  status: string;
  source: string;
  target: string | null;
  expectedIntervalSeconds: number;
  lastSampleAt: string | null;
  probe: ProbeObservation | null;
  lastFailure?: ProbeObservation | null;
  matchesPublication: boolean;
  incidents: { id: number; title: string; severity: string; state: string }[];
  maintenance: { id: number; title: string }[];
  checks?: { id: string; name: string; kind: string; status: string; observedAt?: string; target?: string; probe?: ProbeObservation; detail?: string }[];
}

function object(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function display(value: unknown): string {
  return typeof value === 'number' || typeof value === 'boolean' ? String(value) : text(value);
}

function plain(value: unknown): string {
  return text(value).replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim();
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === 'string') : [];
}

function httpsLink(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : undefined;
  } catch { return undefined; }
}

export function evidenceCards(records: EvidenceRecord[]): EvidenceCard[] {
  const currentChecks = new Map(records.filter(record => record.source === 'independent-probe')
    .map(record => [record.id, record.observedAt]));
  return records.filter(record => record.source !== 'independent-probe-failure'
    || currentChecks.get(record.id) !== record.observedAt).flatMap(record => {
    const data = object(record.evidence);
    const base = {
      source: record.source, observedAt: record.observedAt, record, site: text(data.site) || undefined,
      components: text(data.component) ? [text(data.component)] : [], details: [] as EvidenceCard['details'],
    };
    if (record.source === 'check-result') {
      const monitoring = object(data.monitoring);
      const regions = Array.isArray(data.regions) ? data.regions.map(object) : [];
      return [{
        ...base, title: text(data.name, text(data.checkId, 'Telemetry check')),
        summary: text(data.detail, 'Authenticated telemetry check observation.'),
        severity: data.status === 'outage' ? 'critical' : data.status === 'operational' ? 'info' : 'warning',
        state: text(data.status, 'unknown'),
        details: [
          { label: 'Check', value: text(data.checkId) },
          { label: 'Status', value: text(data.status, 'unknown') },
          { label: 'Source', value: monitoring.checkId ? 'Sitewatch via Application Insights' : 'Authenticated telemetry ingestion' },
          ...regions.map(region => ({
            label: text(region.region, 'Unknown region'),
            value: `${display(region.failures)} failed / ${display(region.samples)} samples${region.lastSeen ? ` / ${text(region.lastSeen)}` : ''}`,
          })),
        ],
      }];
    }
    if (record.source === 'independent-probe' || record.source === 'independent-probe-failure') {
      return [{
        ...base, title: text(data.name, text(data.component, 'HTTP availability check')),
        summary: data.success === true ? 'The HTTP check passed.' : text(data.error, 'The HTTP check failed.'),
        severity: data.success === true ? 'info' : 'critical', state: data.success === true ? 'passed' : 'failed',
        href: httpsLink(data.target),
        details: [
          { label: 'Check', value: record.source === 'independent-probe-failure'
            ? 'Archived failed HTTP check; see monitoring health for the current result'
            : 'Archived status-owned HTTP observation; no longer contributes to current availability' },
          { label: 'Target', value: text(data.target) },
          { label: 'Observer', value: text(data.observer) },
          { label: 'HTTP status', value: display(data.httpStatus) || 'No HTTP response' },
          { label: 'Response time', value: `${display(data.durationMilliseconds)} ms` },
        ],
      }];
    }
    if (record.source === 'azure-alert') {
      const payload = object(data.data);
      const essentials = object(payload.essentials);
      const context = object(payload.alertContext);
      const serviceHealth = object(context.properties);
      const properties = object(payload.customProperties);
      const targets = strings(essentials.alertTargetIDs);
      const conditions = object(context.condition);
      const metrics = Array.isArray(conditions.allOf) ? conditions.allOf.map(object) : [];
      return [{
        ...base, title: plain(serviceHealth.title) || plain(serviceHealth.defaultLanguageTitle) || text(essentials.alertRule, 'Azure monitoring alert'),
        components: text(properties.componentId) ? [text(properties.componentId)] : [],
        workload: text(properties.workload) || text(properties.workloadName),
        resources: targets.map(target => target.split('/').pop() || target),
        summary: plain(serviceHealth.communication) || plain(serviceHealth.defaultLanguageContent)
          || plain(essentials.description) || plain(context.description) || 'An Azure alert was received.',
        severity: text(essentials.severity, 'unknown'), state: text(essentials.monitorCondition, 'received'),
        href: httpsLink(essentials.investigationLink) || httpsLink(context.linkToSearchResultsUI),
        details: [
          { label: 'Signal', value: text(essentials.signalType, 'Azure Monitor') },
          { label: 'Monitor', value: text(essentials.monitoringService) },
          { label: 'Resources', value: targets.map(target => target.split('/').pop()).join(', ') },
          { label: 'Fired', value: text(essentials.firedDateTime) },
          { label: 'Resolved', value: text(essentials.resolvedDateTime) },
          { label: 'Incident type', value: text(serviceHealth.incidentType) },
          { label: 'Stage', value: text(serviceHealth.stage) },
          { label: 'Tracking ID', value: text(serviceHealth.trackingId) },
          { label: 'Service', value: text(serviceHealth.service) },
          { label: 'Region', value: text(serviceHealth.region) },
          ...metrics.slice(0, 30).map(metric => ({
            label: text(metric.metricName, 'Alert condition'),
            value: `${display(metric.metricValue)} ${text(metric.operator)} ${display(metric.threshold)}${text(metric.timeAggregation) ? ` (${text(metric.timeAggregation)})` : ''}`.trim(),
          })),
        ].filter(detail => detail.value),
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
          summary: plain(entry.description),
          severity: 'advisory', state: 'published', href: httpsLink(entry.link),
          details: [{ label: 'Published', value: text(entry.publishedAt) }, { label: 'Coverage', value: text(data.coverage) }].filter(detail => detail.value),
        };
      });
    }
    if (record.source === 'github-issues')
      return [{
        ...base, title: 'GitHub incident publishing', summary: text(data.summary, 'No synchronization summary is available.'),
        severity: data.status === 'healthy' ? 'info' : 'warning', state: text(data.status, 'unknown'),
        href: httpsLink(`https://github.com/${text(data.repository)}`),
        details: [
          { label: 'Imported incidents', value: display(data.imported) },
          { label: 'Errors', value: display(data.errors) },
          ...strings(data.problems).slice(0, 20).map(value => ({ label: 'Problem', value })),
        ].filter(detail => detail.value),
      }];
    if (record.source === 'cloudflare-workers-usage')
      return [{
        ...base, title: 'Cloudflare Workers free allowance',
        summary: text(data.summary, 'Workers account usage could not be checked.'),
        severity: data.status === 'healthy' ? 'info' : 'warning', state: text(data.status, 'unknown'),
        details: [
          { label: 'Estimated requests today (UTC)', value: display(data.Requests ?? data.requests) },
          { label: 'Free daily allowance', value: display(data.FreeDailyAllowance ?? data.freeDailyAllowance) },
          { label: 'Coverage', value: text(data.coverage) },
        ].filter(detail => detail.value),
      }];
    if (record.source === 'sitewatch') {
      const regions = Array.isArray(data.regions) ? data.regions : [];
      return [{
        ...base, title: text(data.component, 'Workload availability observations'),
        summary: regions.length ? `${regions.length} regional observations from Application Insights. These are private context, not the independent public HTTP check.` : 'No regional samples were reported.',
        severity: 'info', state: 'observed',
        details: regions.map(object).map(region => ({
          label: text(region.region, 'Unknown region'),
          value: `${display(region.samples)} samples, ${display(region.failures)} failures; last seen ${text(region.lastSeen, 'unknown')}`,
        })),
      }];
    }
    return [{
      ...base, title: record.source, summary: 'Integration evidence; review the source before using it in an incident.', severity: 'info', state: 'received',
      details: Object.entries(data).filter(([, value]) => ['string', 'number', 'boolean'].includes(typeof value))
        .slice(0, 20).map(([label, value]) => ({ label, value: display(value) })),
    }];
  });
}

export function affectedComponents(card: EvidenceCard, knownIds: string[], workloads: Record<string, string[]>, health: ComponentHealth[]): string[] {
  const inferred = [...card.components, ...(workloads[card.workload || ''] || [])];
  for (const check of health) {
    if (!check.target) continue;
    const resource = new URL(check.target).hostname.split('.')[0].toLowerCase();
    if (card.resources?.some(name => name.toLowerCase() === resource)) inferred.push(check.id);
  }
  return [...new Set(inferred)].filter(id => knownIds.includes(id));
}

export function incidentSeed(card: EvidenceCard, components: string[], names: string[]): {
  title: string; components: string[]; severity: string; state: string; message: string; context: string;
  evidence: { source: string; id: string; observedAt: string };
} {
  const recovering = ['resolved', 'passed', 'clear', 'operational'].includes(card.state.toLowerCase());
  const services = names.length ? names.join(', ') : 'the affected services';
  return {
    title: card.title.slice(0, 200), components,
    severity: ['failed', 'outage'].includes(card.state) || card.severity === 'critical' || /^sev[01]$/i.test(card.severity) ? 'outage' : 'degraded',
    state: recovering ? 'monitoring' : 'investigating',
    message: recovering ? `We are monitoring recovery of ${services}.` : `We are investigating reports affecting ${services}. We will provide an update when more information is available.`,
    context: `${card.source} / ${card.observedAt}\n${card.title}\n${card.summary}`.slice(0, 20000),
    evidence: { source: card.source, id: card.record.id, observedAt: card.observedAt },
  };
}
