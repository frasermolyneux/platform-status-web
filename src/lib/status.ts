import type { ComponentAvailability, HealthStatus } from './types';

const order: Record<HealthStatus, number> = {
  operational: 0,
  unknown: 1,
  degraded: 2,
  outage: 3
};

export function overallStatus(components: ComponentAvailability[]): HealthStatus {
  if (components.length === 0) return 'unknown';
  return components.reduce<HealthStatus>(
    (worst, c) => (order[c.current] > order[worst] ? c.current : worst),
    'operational'
  );
}

export function statusLabel(status: HealthStatus): string {
  switch (status) {
    case 'operational':
      return 'All systems operational';
    case 'degraded':
      return 'Degraded performance';
    case 'outage':
      return 'Service outage';
    default:
      return 'Status unknown';
  }
}
