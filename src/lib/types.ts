export type HealthStatus = 'operational' | 'degraded' | 'outage' | 'unknown';

export interface AvailabilityPoint {
  /** ISO-8601 UTC timestamp for the bucket start. */
  timestamp: string;
  /** Success ratio in [0, 1]. */
  successRate: number;
  status: HealthStatus;
}

export interface ComponentAvailability {
  componentId: string;
  /** Most recent status. */
  current: HealthStatus;
  /** Rolling success ratio across the returned window in [0, 1]. */
  uptime: number;
  /** Chronological buckets, oldest first. */
  history: AvailabilityPoint[];
}

export type IncidentStatus = 'investigating' | 'identified' | 'monitoring' | 'resolved';
export type IncidentImpact = 'none' | 'minor' | 'major' | 'critical';

export interface Incident {
  id: string | number;
  title: string;
  url: string;
  status: IncidentStatus;
  impact: IncidentImpact;
  createdAt: string;
  updatedAt: string;
  /** Component ids the incident is tagged against. */
  componentIds: string[];
  body?: string;
}
