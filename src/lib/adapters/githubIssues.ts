import type { Incident, IncidentImpact, IncidentStatus } from '../types';
import type { SiteConfig } from '../../config/sites';

/**
 * GitHub Issues incident adapter (Phase 1 stub).
 *
 * Real implementation will call the GitHub REST API
 * (`GET /repos/{owner}/{repo}/issues?labels=incident&state=all`) and parse
 * status / impact labels. For Phase 1 we return mock incidents.
 */
export interface IncidentQuery {
  /** Include resolved incidents within this many days. */
  resolvedWithinDays?: number;
}

export async function getIncidents(
  site: SiteConfig,
  _query: IncidentQuery = { resolvedWithinDays: 7 }
): Promise<Incident[]> {
  const now = Date.now();
  return [
    {
      id: 42,
      title: `Elevated error rate on ${site.components[0]?.name ?? 'service'}`,
      url: `https://github.com/${site.githubRepo}/issues/42`,
      status: 'monitoring' satisfies IncidentStatus,
      impact: 'minor' satisfies IncidentImpact,
      createdAt: new Date(now - 3 * 60 * 60_000).toISOString(),
      updatedAt: new Date(now - 30 * 60_000).toISOString(),
      componentIds: site.components[0] ? [site.components[0].id] : [],
      body: 'We are observing elevated error rates and are monitoring the deployed mitigation.'
    },
    {
      id: 41,
      title: 'Scheduled maintenance completed',
      url: `https://github.com/${site.githubRepo}/issues/41`,
      status: 'resolved',
      impact: 'none',
      createdAt: new Date(now - 26 * 60 * 60_000).toISOString(),
      updatedAt: new Date(now - 24 * 60 * 60_000).toISOString(),
      componentIds: site.components.map((c) => c.id)
    }
  ];
}
