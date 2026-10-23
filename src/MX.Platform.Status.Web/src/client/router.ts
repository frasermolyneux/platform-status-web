type RouteHandler = (params: Record<string, string>) => void;

interface Route {
  pattern: RegExp;
  keys: string[];
  handler: RouteHandler;
}

const routes: Route[] = [];
let renderedLocation: string | undefined;

export function addRoute(path: string, handler: RouteHandler): void {
  const keys: string[] = [];
  const pattern = new RegExp(
    '^' + path.replace(/:(\w+)/g, (_, key) => { keys.push(key); return '([^/]+)'; }) + '$'
  );
  routes.push({ pattern, keys, handler });
}

export function navigate(path: string): void {
  history.pushState(null, '', path);
  resolve();
}

export function resolve(): void {
  const path = location.pathname;
  renderedLocation = path + location.search;
  document.querySelectorAll<HTMLAnchorElement>('.site-nav a').forEach(link => {
    const current = link.pathname === path || link.pathname === '/' && /^\/(incident|component)\//.test(path)
      || link.pathname === '/ops' && path.startsWith('/ops/');
    if (current) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  });
  for (const route of routes) {
    const match = path.match(route.pattern);
    if (match) {
      const params: Record<string, string> = {};
      route.keys.forEach((key, i) => { params[key] = match[i + 1]; });
      route.handler(params);
      return;
    }
  }
  // Fallback to index
  routes[0]?.handler({});
}

window.addEventListener('popstate', () => {
  if (renderedLocation !== location.pathname + location.search) resolve();
});
