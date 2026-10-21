import { PublicClientApplication, InteractionRequiredAuthError } from '@azure/msal-browser';
import { fetchStatus, type ComponentDto } from './api';

interface Incident {
  id: number;
  revision: number;
  title: string;
  components: string[];
  severity: string;
  state: string;
  published: boolean;
  scheduledStart?: string;
  scheduledEnd?: string;
}

let authentication: PublicClientApplication | undefined;

function element<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string): HTMLElementTagNameMap[K] {
  const result = document.createElement(tag);
  if (text !== undefined) result.textContent = text;
  return result;
}

function flatten(components: ComponentDto[]): ComponentDto[] {
  return components.flatMap(component => [component, ...flatten(component.children || [])]);
}

export async function renderOperations(): Promise<void> {
  const root = document.getElementById('app');
  if (!root) return;
  root.replaceChildren(element('h2', 'Status operations'));
  const notice = element('p', 'Loading authentication configuration...');
  notice.setAttribute('role', 'status');
  root.append(notice);
  try {
    const configuration = await fetch('/api/auth/config', { cache: 'no-store' });
    if (!configuration.ok) throw new Error('Authentication configuration is unavailable.');
    const config = await configuration.json();
    if (!config.enabled) {
      notice.textContent = 'Operator authentication has not been configured. Public status remains available.';
      return;
    }
    if (!authentication) {
      authentication = new PublicClientApplication({
        auth: {
          clientId: config.clientId,
          authority: `https://login.microsoftonline.com/${config.tenantId}`,
          redirectUri: `${location.origin}/ops`,
          postLogoutRedirectUri: location.origin,
        },
        cache: { cacheLocation: 'sessionStorage' },
      });
      await authentication.initialize();
      await authentication.handleRedirectPromise();
    }
    const client = authentication;
    const signIn = element('button', 'Sign in with Microsoft Entra ID');
    signIn.addEventListener('click', () => {
      client.loginRedirect({ scopes: [config.scope] }).catch(error => {
        notice.textContent = error instanceof Error ? error.message : 'Sign-in failed.';
      });
    });
    const account = client.getAllAccounts()[0];
    if (!account) {
      notice.textContent = 'An explicitly assigned reader or operator role is required.';
      root.append(signIn);
      return;
    }
    const roles = account.idTokenClaims?.roles;
    const canWrite = Array.isArray(roles) && roles.includes('Status.Operator');
    let token: string;
    try {
      token = (await client.acquireTokenSilent({ account, scopes: [config.scope] })).accessToken;
    } catch (error) {
      if (!(error instanceof InteractionRequiredAuthError)) throw error;
      notice.textContent = 'Sign in again to access operations.';
      root.append(signIn);
      return;
    }
    const request = async (path: string, init?: RequestInit): Promise<Response> => {
      const response = await fetch(path, {
        ...init, cache: 'no-store',
        headers: { ...init?.headers, Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      });
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.error || `Operation failed (${response.status}).`);
      }
      return response;
    };
    const [status, incidentsResponse, runtimeResponse] = await Promise.all([
      fetchStatus(), request('/api/ops/incidents'), request('/api/ops/runtime'),
    ]);
    const incidents: Incident[] = await incidentsResponse.json();
    const runtime = await runtimeResponse.json();
    notice.textContent = `${status.site.displayName}. ${runtime.writable ? 'Incident publishing is available.' : 'Read-only: the writer is unavailable.'}`;
    const signOut = element('button', 'Sign out');
    signOut.addEventListener('click', () => {
      client.logoutRedirect({ account }).catch(() => { notice.textContent = 'Sign-out failed.'; });
    });
    root.append(signOut);
    const editor = element('section');
    const openEditor = (incident?: Incident): void => {
      editor.replaceChildren(element('h3', incident ? `Update incident #${incident.id}` : 'Create an incident or maintenance window'));
      const form = element('form');
      const field = (label: string, control: HTMLElement): void => {
        const wrapper = element('label', label);
        wrapper.append(control);
        form.append(wrapper);
      };
      const title = element('input');
      title.required = true;
      title.maxLength = 200;
      title.value = incident?.title || '';
      field('Title', title);
      const components = element('select');
      components.multiple = true;
      components.required = true;
      for (const component of flatten(status.components)) {
        const option = element('option', component.name);
        option.value = component.id;
        option.selected = incident?.components.includes(component.id) || false;
        components.append(option);
      }
      field('Affected components', components);
      const severity = element('select');
      for (const value of ['degraded', 'outage', 'maintenance']) {
        const option = element('option', value);
        option.value = value;
        option.selected = incident?.severity === value;
        severity.append(option);
      }
      field('Severity / type', severity);
      const state = element('select');
      for (const value of ['investigating', 'identified', 'monitoring', 'resolved']) {
        const option = element('option', value);
        option.value = value;
        option.selected = incident?.state === value;
        state.append(option);
      }
      field('State', state);
      const message = element('textarea');
      message.required = true;
      message.maxLength = 20000;
      field('New update (plain text; this becomes public when published)', message);
      const start = element('input');
      const end = element('input');
      start.type = end.type = 'datetime-local';
      const toLocal = (value?: string): string => value
        ? new Date(new Date(value).getTime() - new Date(value).getTimezoneOffset() * 60000).toISOString().slice(0, 16) : '';
      start.value = toLocal(incident?.scheduledStart);
      end.value = toLocal(incident?.scheduledEnd);
      field('Maintenance start (your local time)', start);
      field('Maintenance end (your local time)', end);
      const published = element('input');
      published.type = 'checkbox';
      published.checked = incident?.published || false;
      field('Publish this incident and its update history to the public page', published);
      const save = element('button', 'Save update');
      save.type = 'submit';
      save.disabled = !runtime.writable || !canWrite;
      form.append(save);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        save.disabled = true;
        try {
          await request(`/api/ops/incidents${incident ? `/${incident.id}` : ''}`, {
            method: incident ? 'PUT' : 'POST',
            body: JSON.stringify({
              title: title.value, components: Array.from(components.selectedOptions).map(option => option.value),
              severity: severity.value, state: state.value, message: message.value, published: published.checked,
              revision: incident?.revision || 0,
              scheduledStart: severity.value === 'maintenance' && start.value ? new Date(start.value).toISOString() : null,
              scheduledEnd: severity.value === 'maintenance' && end.value ? new Date(end.value).toISOString() : null,
            }),
          });
          await renderOperations();
        } catch (error) {
          notice.textContent = error instanceof Error ? error.message : 'Unable to save the incident.';
          save.disabled = !runtime.writable || !canWrite;
        }
      });
      editor.append(form);
    };
    const create = element('button', 'New incident / maintenance');
    create.disabled = !canWrite;
    create.addEventListener('click', () => openEditor());
    root.append(create, editor);
    const list = element('section');
    list.append(element('h3', 'Incidents and maintenance'));
    for (const incident of incidents) {
      const button = element('button', `#${incident.id} ${incident.title} - ${incident.state} (${incident.published ? 'public' : 'draft'})`);
      button.disabled = !canWrite;
      button.addEventListener('click', () => openEditor(incident));
      list.append(button);
    }
    root.append(list);
    const evidenceButton = element('button', 'Load restricted monitoring evidence');
    evidenceButton.addEventListener('click', async () => {
      try {
        const evidence = await (await request('/api/ops/evidence')).json();
        const details = element('pre', JSON.stringify(evidence, null, 2));
        details.className = 'evidence';
        evidenceButton.replaceWith(details);
      } catch (error) {
        notice.textContent = error instanceof Error ? error.message : 'Unable to load evidence.';
      }
    });
    root.append(evidenceButton);
  } catch (error) {
    notice.textContent = error instanceof Error ? error.message : 'Operations are unavailable.';
  }
}
