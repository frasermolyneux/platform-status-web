import type { ComponentAvailability, HealthStatus } from '../types';
import type { SiteConfig } from '../../config/sites';

/**
 * App Insights availability adapter (Phase 1 stub).
 *
 * Real implementation will query the Application Insights REST API
 * (availabilityResults) for the configured resource id. For now we emit
 * deterministic mock data so the UI can be built and reviewed.
 */
export interface AvailabilityQuery {
  /** Window length in hours (e.g. 24, 168). */
  windowHours: number;
  /** Bucket size in minutes. */
  bucketMinutes: number;
}

export async function getAvailability(
  site: SiteConfig,
  query: AvailabilityQuery = { windowHours: 24, bucketMinutes: 60 }
): Promise<ComponentAvailability[]> {
  const buckets = Math.max(1, Math.floor((query.windowHours * 60) / query.bucketMinutes));
  const now = Date.now();

  return site.components.map((component, componentIndex) => {
    const history = Array.from({ length: buckets }, (_, i) => {
      const bucketStart = now - (buckets - 1 - i) * query.bucketMinutes * 60_000;
      const successRate = mockSuccessRate(site.slug, component.id, componentIndex, i);
      return {
        timestamp: new Date(bucketStart).toISOString(),
        successRate,
        status: toStatus(successRate)
      };
    });
    const uptime = history.reduce((a, b) => a + b.successRate, 0) / history.length;
    return {
      componentId: component.id,
      current: history[history.length - 1].status,
      uptime,
      history
    };
  });
}

function toStatus(successRate: number): HealthStatus {
  if (successRate >= 0.99) return 'operational';
  if (successRate >= 0.9) return 'degraded';
  if (successRate > 0) return 'outage';
  return 'unknown';
}

/** Cheap deterministic pseudo-random so mocks are stable between renders. */
function mockSuccessRate(siteSlug: string, componentId: string, ci: number, bi: number): number {
  const seed = hash(`${siteSlug}:${componentId}:${bi}`);
  const jitter = (seed % 1000) / 10000; // 0..0.1
  // Inject an "outage" for one component to make the UI representative.
  if (ci === 1 && bi > 4 && bi < 8) return 0.6 - jitter;
  return 1 - jitter * 0.2;
}

function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return Math.abs(h);
}
