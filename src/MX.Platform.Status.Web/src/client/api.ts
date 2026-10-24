export interface StatusResponse {
  schemaVersion: number;
  site: { id: string; displayName: string; tagline?: string; links?: { label: string; href: string }[] };
  generatedAt: string;
  dataFreshness?: { appInsightsAt?: string; historyAt?: string; stale?: boolean };
  summary: { status: string; message?: string };
  components: ComponentDto[];
  incidents: IncidentDto[];
  maintenance: MaintenanceDto[];
  banners?: BannerDto[];
}

export interface BannerDto {
  id: number;
  title: string;
  message: string;
  tone: 'info' | 'warning' | 'critical';
  startsAt?: string;
  expiresAt?: string;
  updatedAt: string;
}

export interface ComponentDto {
  id: string;
  name: string;
  description?: string;
  link?: string;
  kind: 'leaf' | 'group';
  status: string;
  lastSampleAt?: string | null;
  uptimeWindowDays: number;
  uptimeRatio: number | null;
  history: HistoryDayDto[];
  openIncidentIds?: number[];
  children?: ComponentDto[];
  checks?: { id: string; name: string; kind: string; status: string; observedAt?: string | null }[];
}

export interface HistoryDayDto {
  date: string;
  status: string;
  uptime?: number | null;
  total?: number | null;
  failed?: number | null;
  incidentIds?: number[];
}

export interface IncidentDto {
  id: number;
  aliases?: number[];
  title: string;
  url: string;
  sourceUrl?: string;
  components: string[];
  severity: string;
  state: string;
  createdAt: string;
  startedAt?: string;
  resolvedAt?: string | null;
  updates: { at: string; state: string; severity?: string; body: string; author?: string }[];
}

export interface MaintenanceDto {
  id: number;
  title: string;
  url: string;
  components: string[];
  scheduledStart: string;
  scheduledEnd: string;
  state: string;
  body?: string;
}

export async function fetchStatus(): Promise<StatusResponse> {
  const res = await fetch('/api/status', { cache: 'no-store', signal: AbortSignal.timeout(10_000) });
  if (!res.ok) throw new Error(`Status API returned ${res.status}`);
  return res.json();
}
