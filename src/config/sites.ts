/**
 * Site registry. Each entry produces a route segment (e.g. /xtremeidiots).
 * Phase 1: declared statically; later phases can hydrate from external config.
 */

export interface MonitoredComponent {
  /** Stable id used for App Insights availability test name + GitHub Issues label. */
  id: string;
  name: string;
  description?: string;
  /** Optional public URL of the component (shown on status page). */
  url?: string;
}

export interface SiteConfig {
  /** URL slug, used in routes: /[slug]. */
  slug: string;
  /** Display name shown in the UI. */
  name: string;
  /** Short tagline / description. */
  description: string;
  /** GitHub repo (owner/name) used for incident issues. */
  githubRepo: string;
  /** Application Insights resource id used to query availability tests. */
  appInsightsResourceId?: string;
  /** Brand accent colour (CSS variable value). */
  accentColor: string;
  components: MonitoredComponent[];
}

export const sites: SiteConfig[] = [
  {
    slug: 'xtremeidiots',
    name: 'XtremeIdiots',
    description: 'Gaming community platform services and APIs.',
    githubRepo: 'frasermolyneux/xtremeidiots-status',
    accentColor: '#e63946',
    components: [
      {
        id: 'portal-web',
        name: 'Portal Web',
        description: 'Public web portal',
        url: 'https://portal.xtremeidiots.com'
      },
      {
        id: 'repository-api',
        name: 'Repository API',
        description: 'Core data API'
      },
      {
        id: 'servers-api',
        name: 'Servers API',
        description: 'Game server integration API'
      }
    ]
  },
  {
    slug: 'molyneux',
    name: 'Molyneux.io',
    description: 'Personal platform and project services.',
    githubRepo: 'frasermolyneux/molyneux-status',
    accentColor: '#457b9d',
    components: [
      {
        id: 'molyneux-io',
        name: 'molyneux.io',
        description: 'Personal site',
        url: 'https://molyneux.io'
      },
      {
        id: 'blog',
        name: 'Blog',
        url: 'https://blog.molyneux.io'
      }
    ]
  }
];

export function getSite(slug: string): SiteConfig | undefined {
  return sites.find((s) => s.slug === slug);
}
