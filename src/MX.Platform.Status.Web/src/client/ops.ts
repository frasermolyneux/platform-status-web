import { PublicClientApplication, InteractionRequiredAuthError } from '@azure/msal-browser';
import { fetchStatus, type ComponentDto } from './api';
import { evidenceCards, type EvidenceRecord } from './evidence';
import { formatDateTime } from './time';

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
  sourceUrl?: string;
}

interface Banner {
  id: number;
  revision: number;
  title: string;
  message: string;
  tone: string;
  published: boolean;
  startsAt?: string;
  expiresAt?: string;
}

interface Runtime {
  publisher: boolean;
  writable: boolean;
  github: { repository: string; pollSeconds: number };
  workloads: Record<string, string[]>;
}

type Control = HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement;
let authentication: PublicClientApplication | undefined;

function element<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string, className?: string): HTMLElementTagNameMap[K] {
  const result = document.createElement(tag);
  if (text !== undefined) result.textContent = text;
  if (className) result.className = className;
  return result;
}

function button(text: string, action: () => void, primary = false): HTMLButtonElement {
  const result = element('button', text, primary ? 'button-primary' : '');
  result.type = 'button';
  result.addEventListener('click', action);
  return result;
}

function link(text: string, href: string): HTMLAnchorElement {
  const result = element('a', text);
  result.href = href;
  return result;
}

function flatten(components: ComponentDto[]): ComponentDto[] {
  return components.flatMap(component => [component, ...flatten(component.children || [])]);
}

function toLocal(value?: string): string {
  return value ? new Date(new Date(value).getTime() - new Date(value).getTimezoneOffset() * 60000).toISOString().slice(0, 16) : '';
}

function option(value: string, label = value): HTMLOptionElement {
  const result = element('option', label);
  result.value = value;
  return result;
}

function field(form: HTMLFormElement, label: string, control: Control, name: string): void {
  control.name = name;
  const wrapper = element('label', label, control instanceof HTMLInputElement && control.type === 'checkbox' ? 'checkbox-field' : '');
  wrapper.append(control);
  form.append(wrapper);
}

export async function renderOperations(): Promise<void> {
  const root = document.getElementById('app');
  if (!root) return;
  const heading = element('div', undefined, 'page-heading');
  heading.append(element('p', 'PRIVATE WORKSPACE', 'eyebrow'), element('h2', 'Status operations'),
    element('p', 'Clear signals. Deliberate communication.', 'muted'));
  const notice = element('p', 'Preparing your secure workspace...', 'operator-notice');
  notice.setAttribute('role', 'status');
  root.replaceChildren(heading, notice);
  const message = (text: string, error = false): void => {
    notice.textContent = text;
    notice.className = `operator-notice${error ? ' error' : ''}`;
  };
  try {
    const configuration = await fetch('/api/auth/config', { cache: 'no-store' });
    if (!configuration.ok) throw new Error('Authentication configuration is unavailable. Public status is unaffected.');
    const config: { enabled: boolean; clientId: string; tenantId: string; scope: string } = await configuration.json();
    if (!config.enabled) {
      message('Operator authentication is not configured. Public status remains available.');
      return;
    }
    if (!authentication) {
      const instance = new PublicClientApplication({
        auth: {
          clientId: config.clientId, authority: `https://login.microsoftonline.com/${config.tenantId}`,
          redirectUri: `${location.origin}/ops`, postLogoutRedirectUri: location.origin,
        },
        cache: { cacheLocation: 'sessionStorage' },
      });
      await instance.initialize();
      const result = await instance.handleRedirectPromise();
      if (result?.account) instance.setActiveAccount(result.account);
      authentication = instance;
    }
    if (location.pathname !== '/ops') return;
    const client = authentication;
    const account = client.getActiveAccount() || client.getAllAccounts()[0];
    const signIn = button('Continue with Microsoft', () => {
      client.loginRedirect({ scopes: [config.scope], account }).catch(error => {
        message(error instanceof Error ? error.message : 'Sign-in failed.', true);
      });
    }, true);
    if (!account) {
      message('Sign in to review monitoring evidence, manage incidents and publish announcements.');
      const login = element('section', undefined, 'login-card');
      login.append(element('span', 'FOR READERS & OPERATORS', 'eyebrow'), element('h3', 'Your operations, in one place'),
        element('p', 'Use your assigned Microsoft Entra account. Monitoring evidence stays private; nothing is published automatically.'),
        signIn, link('Back to public status', '/'));
      root.append(login);
      return;
    }
    client.setActiveAccount(account);
    const roles = account.idTokenClaims?.roles;
    const canWrite = Array.isArray(roles) && roles.includes('Status.Operator');
    const reauthenticate = (): void => {
      signIn.textContent = 'Reconnect Microsoft session';
      if (!signIn.isConnected) notice.after(signIn);
      message('Your session needs attention. Your form drafts are saved in this tab; reconnect to continue.', true);
    };
    const request = async (path: string, init?: RequestInit): Promise<Response> => {
      let token: string;
      try {
        token = (await client.acquireTokenSilent({ account, scopes: [config.scope] })).accessToken;
      } catch (error) {
        if (error instanceof InteractionRequiredAuthError) reauthenticate();
        throw error;
      }
      const headers = new Headers(init?.headers);
      headers.set('Authorization', `Bearer ${token}`);
      headers.set('Content-Type', 'application/json');
      const response = await fetch(path, { ...init, headers, cache: 'no-store' });
      if (!response.ok) {
        if (response.status === 401) reauthenticate();
        if (response.status === 403) throw new Error('Your account does not have the required status role. Contact an administrator.');
        const body: { error?: string } = await response.json().catch(() => ({}));
        throw new Error(body.error || `Operation failed (${response.status}). Your form has not been cleared.`);
      }
      return response;
    };
    const toolbar = element('div', undefined, 'operator-toolbar');
    toolbar.append(element('span', `${account.name || account.username} / ${canWrite ? 'Operator' : 'Reader'}`, 'muted'),
      button('Sign out', () => {
        client.logoutRedirect({ account }).catch(error => message(error instanceof Error ? error.message : 'Sign-out failed.', true));
      }));
    root.append(toolbar);
    const [status, incidentsResponse, bannersResponse, runtimeResponse, evidenceResponse] = await Promise.all([
      fetchStatus(), request('/api/ops/incidents'), request('/api/ops/banners'), request('/api/ops/runtime'), request('/api/ops/evidence'),
    ]);
    if (location.pathname !== '/ops') return;
    document.title = `${status.site.displayName} / Operations`;
    const siteTitle = document.getElementById('site-title');
    if (siteTitle) siteTitle.textContent = status.site.displayName;
    let incidents: Incident[] = await incidentsResponse.json();
    let banners: Banner[] = await bannersResponse.json();
    let runtime: Runtime = await runtimeResponse.json();
    let evidence: EvidenceRecord[] = await evidenceResponse.json();
    const overview = element('div', undefined, 'operator-stats');
    const editor = element('section', undefined, 'editor-panel');
    editor.hidden = true;
    const incidentSection = element('section', undefined, 'panel');
    incidentSection.id = 'operator-incidents';
    const bannerSection = element('section', undefined, 'panel');
    bannerSection.id = 'operator-banners';
    const evidenceSection = element('section', undefined, 'panel');
    evidenceSection.id = 'operator-evidence';
    const actions = element('div', undefined, 'operator-actions');
    const refresh = button('Refresh workspace', () => {
      void refreshData().catch(error => message(error instanceof Error ? error.message : 'The workspace could not be refreshed.', true));
    });
    const createIncident = button('New incident', () => openIncident(), true);
    const createBanner = button('New announcement', () => openBanner(), true);
    actions.append(createBanner, createIncident, refresh);
    const navigation = element('nav', undefined, 'workspace-nav');
    navigation.setAttribute('aria-label', 'Workspace sections');
    navigation.append(link('Monitoring evidence', '#operator-evidence'), link('Announcements', '#operator-banners'), link('Incidents', '#operator-incidents'));
    root.append(actions, overview, navigation, editor, evidenceSection, bannerSection, incidentSection);

    function rememberDraft(form: HTMLFormElement, key: string, revision: number): void {
      const controls = (): Control[] => Array.from(form.querySelectorAll<Control>('input,select,textarea'));
      try {
        const saved: { revision?: number; values?: Record<string, unknown> } = JSON.parse(sessionStorage.getItem(key) || '{}');
        const restore = (): void => {
          for (const control of controls()) {
            const value = saved.values?.[control.name];
            if (control instanceof HTMLInputElement && control.type === 'checkbox' && typeof value === 'boolean')
              control.checked = value;
            else if (control instanceof HTMLSelectElement && control.multiple && Array.isArray(value))
              Array.from(control.options).forEach(item => { item.selected = value.includes(item.value); });
            else if (typeof value === 'string') control.value = value;
          }
        };
        if (saved.revision !== undefined && saved.revision !== revision) {
          const warning = element('div', undefined, 'stale-warning');
          warning.append(element('p', 'An older draft exists. The form currently shows the latest saved version. Review it before restoring your draft.'),
            button('Restore older draft', () => {
              restore();
              warning.remove();
              form.dispatchEvent(new Event('input', { bubbles: true }));
              message('Older draft restored. Review your changes carefully before applying them to the latest saved version.');
            }));
          form.prepend(warning);
        } else restore();
        form.addEventListener('input', () => {
          const draft: Record<string, string | boolean | string[]> = {};
          for (const control of controls())
            draft[control.name] = control instanceof HTMLInputElement && control.type === 'checkbox' ? control.checked
              : control instanceof HTMLSelectElement && control.multiple ? Array.from(control.selectedOptions).map(item => item.value) : control.value;
          try { sessionStorage.setItem(key, JSON.stringify({ revision, values: draft })); }
          catch { message('This browser cannot save form drafts. Keep this tab open until your update is saved.', true); }
        });
      } catch { message('A saved draft could not be restored. Check your form before publishing.', true); }
    }

    function openForm(title: string, kind: string, id?: number): { form: HTMLFormElement; save: HTMLButtonElement; key: string } {
      editor.hidden = false;
      editor.replaceChildren(element('p', 'COMMUNICATION STUDIO', 'eyebrow'), element('h3', title));
      const form = element('form');
      const save = element('button', 'Save changes', 'button-primary');
      save.type = 'submit';
      save.disabled = !canWrite || !runtime.writable;
      editor.append(form);
      return { form, save, key: `status-draft:${account!.homeAccountId}:${status.site.id}:${kind}:${id || 'new'}` };
    }

    function finishForm(form: HTMLFormElement, save: HTMLButtonElement, key: string, revision: number, submit: () => Promise<void>): void {
      rememberDraft(form, key, revision);
      const controls = element('div', undefined, 'form-actions');
      controls.append(save, button('Close editor (keep draft)', () => { editor.hidden = true; }));
      form.append(controls);
      editor.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
      form.addEventListener('submit', async event => {
        event.preventDefault();
        save.disabled = true;
        try {
          await submit();
          let draftWarning = '';
          try { sessionStorage.removeItem(key); }
          catch { draftWarning = ' The browser could not clear its old draft; close this tab when finished.'; }
          editor.hidden = true;
          try {
            await refreshData();
            message(`Changes saved. Public updates appear after the next publication, normally within one minute.${draftWarning}`, !!draftWarning);
          } catch {
            message(`Your changes were saved, but the workspace could not refresh. Refresh it before editing again.${draftWarning}`, true);
          }
        } catch (error) {
          message(error instanceof Error ? error.message : 'Unable to save changes. Your form has been preserved.', true);
        } finally { save.disabled = !canWrite || !runtime.writable; }
      });
    }

    function openIncident(incident?: Incident): void {
      const { form, save, key } = openForm(incident ? `Update incident #${incident.id}` : 'Create an incident or maintenance window', 'incident', incident?.id);
      const title = element('input');
      title.required = true;
      title.maxLength = 200;
      title.value = incident?.title || '';
      field(form, 'Title', title, 'title');
      const components = element('select');
      components.multiple = components.required = true;
      components.size = Math.min(7, flatten(status.components).length);
      for (const component of flatten(status.components)) {
        const item = option(component.id, component.name);
        item.selected = incident?.components.includes(component.id) || false;
        components.append(item);
      }
      field(form, 'Affected services (hold Ctrl or Command to select several)', components, 'components');
      const severity = element('select');
      severity.append(...['degraded', 'outage', 'maintenance'].map(value => option(value)));
      severity.value = incident?.severity || 'degraded';
      field(form, 'Impact / type', severity, 'severity');
      const state = element('select');
      state.append(...['investigating', 'identified', 'monitoring', 'resolved'].map(value => option(value)));
      state.value = incident?.state || 'investigating';
      field(form, 'Current state', state, 'state');
      const update = element('textarea');
      update.required = true;
      update.maxLength = 20000;
      field(form, 'New update (plain text; only this update is approved when you publish)', update, 'message');
      const start = element('input');
      const end = element('input');
      start.type = end.type = 'datetime-local';
      start.value = toLocal(incident?.scheduledStart);
      end.value = toLocal(incident?.scheduledEnd);
      field(form, 'Maintenance start (your local time)', start, 'scheduledStart');
      field(form, 'Maintenance end (your local time)', end, 'scheduledEnd');
      const published = element('input');
      published.type = 'checkbox';
      published.checked = incident?.published || false;
      field(form, 'Publish this incident and the new update to the public page', published, 'published');
      finishForm(form, save, key, incident?.revision || 0, async () => {
        await request(`/api/ops/incidents${incident ? `/${incident.id}` : ''}`, {
          method: incident ? 'PUT' : 'POST',
          body: JSON.stringify({
            title: title.value, components: Array.from(components.selectedOptions).map(item => item.value),
            severity: severity.value, state: state.value, message: update.value, published: published.checked, revision: incident?.revision || 0,
            scheduledStart: severity.value === 'maintenance' && start.value ? new Date(start.value).toISOString() : null,
            scheduledEnd: severity.value === 'maintenance' && end.value ? new Date(end.value).toISOString() : null,
          }),
        });
      });
    }

    function openBanner(banner?: Banner): void {
      const { form, save, key } = openForm(banner ? `Edit announcement #${banner.id}` : 'Create a public announcement', 'banner', banner?.id);
      const title = element('input');
      title.required = true;
      title.maxLength = 120;
      title.value = banner?.title || '';
      field(form, 'Title', title, 'title');
      const body = element('textarea');
      body.required = true;
      body.maxLength = 2000;
      body.value = banner?.message || '';
      field(form, 'Message (plain text)', body, 'message');
      const tone = element('select');
      tone.append(option('info', 'Informational'), option('warning', 'Warning'), option('critical', 'Critical'));
      tone.value = banner?.tone || 'info';
      field(form, 'Appearance', tone, 'tone');
      const start = element('input');
      const end = element('input');
      start.type = end.type = 'datetime-local';
      start.value = toLocal(banner?.startsAt);
      end.value = toLocal(banner?.expiresAt);
      field(form, 'Start (optional, your local time)', start, 'startsAt');
      field(form, 'Expiry (optional, your local time)', end, 'expiresAt');
      const published = element('input');
      published.type = 'checkbox';
      published.checked = banner?.published || false;
      field(form, 'Publish on this brand only (uncheck to withdraw)', published, 'published');
      const preview = element('article');
      const updatePreview = (): void => {
        preview.className = `announcement announcement-${tone.value}`;
        preview.replaceChildren(element('strong', title.value || 'Announcement preview'), element('p', body.value || 'Your message will appear here.'));
      };
      form.append(element('p', 'Public preview', 'eyebrow'), preview);
      finishForm(form, save, key, banner?.revision || 0, async () => {
        await request(`/api/ops/banners${banner ? `/${banner.id}` : ''}`, {
          method: banner ? 'PUT' : 'POST',
          body: JSON.stringify({
            title: title.value, message: body.value, tone: tone.value, published: published.checked, revision: banner?.revision || 0,
            startsAt: start.value ? new Date(start.value).toISOString() : null,
            expiresAt: end.value ? new Date(end.value).toISOString() : null,
          }),
        });
      });
      form.addEventListener('input', updatePreview);
      updatePreview();
    }

    function renderEvidence(): void {
      evidenceSection.replaceChildren(element('p', 'RESTRICTED SIGNALS', 'eyebrow'), element('h3', 'Monitoring evidence'),
        element('p', 'Azure alerts and service advisories are private context, not public incidents. Review and explicitly publish any customer-facing communication.', 'muted'));
      const cards = evidenceCards(evidence);
      const filters = element('div', undefined, 'evidence-filters');
      const source = element('select');
      source.setAttribute('aria-label', 'Filter evidence by source');
      source.append(option('', 'All sources'), ...Array.from(new Set(cards.map(card => card.source))).map(value => option(value)));
      const severity = element('select');
      severity.setAttribute('aria-label', 'Filter evidence by severity');
      severity.append(option('', 'All severities'), ...Array.from(new Set(cards.map(card => card.severity))).map(value => option(value)));
      const state = element('select');
      state.setAttribute('aria-label', 'Filter evidence by state');
      state.append(option('', 'All states'), ...Array.from(new Set(cards.map(card => card.state))).map(value => option(value)));
      filters.append(source, severity, state);
      const list = element('div', undefined, 'evidence-grid');
      const render = (): void => {
        list.replaceChildren();
        const selected = cards.filter(card => (!source.value || card.source === source.value)
          && (!severity.value || card.severity === severity.value) && (!state.value || card.state === state.value));
        for (const card of selected) {
          const article = element('article', undefined, 'evidence-card');
          const meta = element('div', undefined, 'incident-meta');
          meta.append(element('span', card.source, 'badge badge-state'), element('span', card.severity, 'badge'),
            element('span', card.state, 'badge'), element('span', formatDateTime(card.observedAt)));
          article.append(meta, element('h4', card.title), element('p', card.summary || 'No description was supplied.'));
          if (card.href) article.append(link('Open source', card.href));
          const details = element('details');
          details.append(element('summary', 'Technical details'), element('pre', JSON.stringify(card.record.evidence, null, 2), 'evidence'));
          article.append(details);
          list.append(article);
        }
        if (!selected.length) list.append(element('p', cards.length ? 'No evidence matches these filters.' : 'No integration evidence has arrived yet.', 'empty-state'));
      };
      [source, severity, state].forEach(control => control.addEventListener('change', render));
      evidenceSection.append(filters, list);
      render();
    }

    function renderLists(): void {
      createBanner.disabled = createIncident.disabled = !canWrite || !runtime.writable;
      overview.replaceChildren();
      for (const [value, label] of [
        [runtime.writable ? 'Ready' : 'Read-only', 'Publication writer'],
        [String(incidents.filter(incident => incident.published && incident.state !== 'resolved').length), 'Open public incidents'],
        [String(banners.filter(banner => banner.published && (!banner.expiresAt || new Date(banner.expiresAt) > new Date())).length), 'Enabled announcements'],
        [String(new Set(evidence.map(item => item.source)).size), 'Reporting integrations'],
      ]) {
        const stat = element('div', undefined, 'stat-card');
        stat.append(element('strong', value), element('span', label));
        overview.append(stat);
      }
      incidentSection.replaceChildren(element('p', 'CUSTOMER COMMUNICATION', 'eyebrow'), element('h3', 'Incidents & maintenance'));
      const sourceUrl = `https://github.com/${runtime.github.repository}`;
      const sourceNote = element('p', undefined, 'muted');
      sourceNote.append('GitHub-managed incidents are edited in ', link(runtime.github.repository, sourceUrl),
        `. A trusted author, matching workload labels and status:published are required. Synced every ${runtime.github.pollSeconds / 60} minutes.`);
      incidentSection.append(sourceNote, link('Declare an incident in GitHub', `${sourceUrl}/issues/new/choose`));
      if (!incidents.length) incidentSection.append(element('p', 'No incidents or maintenance windows yet.', 'empty-state'));
      for (const incident of incidents) {
        const row = element('article', undefined, 'management-row');
        const copy = element('div');
        copy.append(element('h4', incident.title), element('p', `${incident.state} / ${incident.severity} / ${incident.published ? 'public' : 'draft'}`, 'muted'));
        const control = incident.sourceUrl ? link('Manage in GitHub', incident.sourceUrl) : button('Edit incident', () => openIncident(incident));
        if (control instanceof HTMLButtonElement) control.disabled = !canWrite || !runtime.writable;
        row.append(copy, control);
        incidentSection.append(row);
      }
      bannerSection.replaceChildren(element('p', 'SITE ANNOUNCEMENTS', 'eyebrow'), element('h3', 'Manual banners'),
        element('p', `Announcements appear on ${status.site.displayName} only. Scheduling and expiry continue to work from the durable public cache.`, 'muted'));
      if (!banners.length) bannerSection.append(element('p', 'No announcements. Create a banner for important notices that do not need an incident.', 'empty-state'));
      for (const banner of banners) {
        const row = element('article', undefined, 'management-row');
        const copy = element('div');
        const phase = !banner.published ? 'draft' : banner.expiresAt && new Date(banner.expiresAt) <= new Date() ? 'expired'
          : banner.startsAt && new Date(banner.startsAt) > new Date() ? 'scheduled' : 'public';
        copy.append(element('h4', banner.title), element('p', `${phase} / ${banner.tone}${banner.expiresAt ? ` / expires ${formatDateTime(banner.expiresAt)}` : ''}`, 'muted'));
        const edit = button('Edit banner', () => openBanner(banner));
        edit.disabled = !canWrite || !runtime.writable;
        row.append(copy, edit);
        bannerSection.append(row);
      }
      renderEvidence();
      message(`${status.site.displayName}. ${runtime.writable ? 'Writer connected.' : 'Writer unavailable; publishing is read-only.'} ${canWrite ? 'You can publish public communications.' : 'Your access is read-only.'}`);
    }

    async function refreshData(): Promise<void> {
      refresh.disabled = true;
      try {
        const responses = await Promise.all([
          request('/api/ops/incidents'), request('/api/ops/banners'), request('/api/ops/runtime'), request('/api/ops/evidence'),
        ]);
        [incidents, banners, runtime, evidence] = await Promise.all(responses.map(response => response.json()));
        renderLists();
      } catch (error) {
        message(error instanceof Error ? error.message : 'The workspace could not be refreshed.', true);
        throw error;
      } finally { refresh.disabled = false; }
    }
    renderLists();
  } catch (error) {
    message(error instanceof Error ? error.message : 'Operations are unavailable. Public status remains available.', true);
    root.append(button('Try again', () => { void renderOperations(); }));
  }
}
