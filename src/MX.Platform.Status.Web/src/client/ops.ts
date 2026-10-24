import { PublicClientApplication, InteractionRequiredAuthError } from '@azure/msal-browser';
import { fetchStatus, type ComponentDto } from './api';
import { affectedComponents, evidenceCards, incidentSeed, type ComponentHealth, type EvidenceCard, type EvidenceRecord } from './evidence';
import { formatDateTime } from './time';
import { button, element, field, link, option, type Control } from './ops-ui';
import { renderTraffic } from './traffic';

interface Incident {
  id: number;
  siteId?: string;
  revision: number;
  title: string;
  components: string[];
  severity: string;
  state: string;
  published: boolean;
  scheduledStart?: string;
  scheduledEnd?: string;
  sourceUrl?: string;
  evidence?: { source: string; id: string; observedAt: string; context: string };
}

interface Banner {
  id: number;
  revision: number;
  title: string;
  message: string;
  tone: string;
  published: boolean;
  archived?: boolean;
  startsAt?: string;
  expiresAt?: string;
}

interface Runtime {
  publisher: boolean;
  writable: boolean;
  github: { enabled: boolean; repository: string; pollSeconds: number };
  workloads: Record<string, string[]>;
}

type EvidenceDraft = ReturnType<typeof incidentSeed>;
let authentication: PublicClientApplication | undefined;
let removeWorkspaceRefresh: (() => void) | undefined;

function readEvidenceDraft(saved: string): EvidenceDraft {
  const seed: EvidenceDraft = JSON.parse(saved);
  if (!seed || typeof seed.title !== 'string' || typeof seed.message !== 'string' || typeof seed.context !== 'string'
    || !Array.isArray(seed.components) || seed.components.some(id => typeof id !== 'string')
    || !seed.evidence || typeof seed.evidence.source !== 'string' || typeof seed.evidence.id !== 'string'
    || typeof seed.evidence.observedAt !== 'string' || !Number.isFinite(Date.parse(seed.evidence.observedAt))
    || !['degraded', 'outage'].includes(seed.severity) || !['investigating', 'monitoring'].includes(seed.state))
    throw new Error('The saved evidence draft is invalid. Return to monitoring evidence to prepare it again.');
  return seed;
}

function flatten(components: ComponentDto[]): ComponentDto[] {
  return components.flatMap(component => [component, ...flatten(component.children || [])]);
}

function toLocal(value?: string): string {
  return value ? new Date(new Date(value).getTime() - new Date(value).getTimezoneOffset() * 60000).toISOString().slice(0, 16) : '';
}

export async function renderOperations(navigateTo: (path: string) => void = path => location.assign(path)): Promise<void> {
  removeWorkspaceRefresh?.();
  const root = document.getElementById('app');
  if (!root) return;
  const path = location.pathname;
  const page = path.split('/')[2] || 'overview';
  const labels: Record<string, string> = { overview: 'Operations', incidents: 'Incidents', banners: 'Banners', evidence: 'Monitoring evidence', traffic: 'Traffic controls' };
  const heading = element('div', undefined, 'page-heading');
  heading.append(element('h2', labels[page] || 'Operations'),
    element('p', 'Manage status updates and review monitoring results.', 'muted'));
  const notice = element('p', 'Preparing your secure workspace...', 'operator-notice');
  notice.setAttribute('role', 'status');
  root.replaceChildren(heading, notice);
  const current = (): boolean => location.pathname === path && root.contains(heading);
  const message = (text: string, error = false): void => {
    notice.textContent = text;
    notice.className = `operator-notice${error ? ' error' : ''}`;
  };
  try {
    const configuration = await fetch('/api/auth/config', { cache: 'no-store', signal: AbortSignal.timeout(10_000) });
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
    if (!current()) return;
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
      login.append(element('h3', 'Sign in to operations'),
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
      const response = await fetch(path, { ...init, headers, cache: 'no-store',
        signal: init?.signal ?? (init?.method && init.method !== 'GET' ? undefined : AbortSignal.timeout(10_000)) });
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
    const [status, incidentsResponse, bannersResponse, runtimeResponse, evidenceResponse, healthResponse] = await Promise.all([
      fetchStatus(), request('/api/ops/incidents'), request('/api/ops/banners'), request('/api/ops/runtime'), request('/api/ops/evidence'), request('/api/ops/health'),
    ]);
    if (!current()) return;
    document.title = `${status.site.displayName} / ${labels[page] || 'Operations'}`;
    const siteTitle = document.getElementById('site-title');
    if (siteTitle) siteTitle.textContent = status.site.displayName;
    let incidents: Incident[] = await incidentsResponse.json();
    let banners: Banner[] = await bannersResponse.json();
    let runtime: Runtime = await runtimeResponse.json();
    let evidence: EvidenceRecord[] = await evidenceResponse.json();
    let health: ComponentHealth[] = await healthResponse.json();
    let refreshTraffic: (() => Promise<void>) | undefined;
    let showArchived = false;
    const evidenceDraftPrefix = `status-evidence-draft:${account.homeAccountId}:${status.site.id}:`;
    const overview = element('div', undefined, 'operator-stats');
    const editor = element('section', undefined, 'editor-panel');
    editor.hidden = true;
    const incidentSection = element('section', undefined, 'panel');
    incidentSection.id = 'operator-incidents';
    const bannerSection = element('section', undefined, 'panel');
    bannerSection.id = 'operator-banners';
    const evidenceSection = element('section', undefined, 'panel');
    evidenceSection.id = 'operator-evidence';
    const healthSection = element('section', undefined, 'panel');
    const draftsSection = element('section', undefined, 'panel');
    draftsSection.id = 'operator-evidence-drafts';
    const actions = element('div', undefined, 'operator-actions');
    const refresh = button('Refresh workspace', () => {
      void refreshData().catch(error => message(error instanceof Error ? error.message : 'The workspace could not be refreshed.', true));
    });
    const createIncident = button('New incident', () => openIncident(), true);
    const createBanner = button('New announcement', () => openBanner(), true);
    if (page === 'banners') actions.append(createBanner);
    if (page === 'incidents') actions.append(createIncident);
    actions.append(refresh);
    const navigation = element('nav', undefined, 'workspace-nav');
    navigation.setAttribute('aria-label', 'Workspace sections');
    for (const [key, label] of Object.entries(labels)) {
      const item = link(label, key === 'overview' ? '/ops' : `/ops/${key}`);
      if (key === page) item.setAttribute('aria-current', 'page');
      navigation.append(item);
    }
    root.append(navigation, actions);
    if (page === 'overview') root.append(overview, healthSection);
    if (page === 'evidence') root.append(healthSection, evidenceSection);
    if (page === 'banners') root.append(editor, bannerSection);
    if (page === 'incidents') root.append(draftsSection, editor, incidentSection);

    function formDraftKey(kind: string, id?: number): string {
      return `status-draft:${account!.homeAccountId}:${status.site.id}:${kind}:${id || 'new'}`;
    }

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
      editor.replaceChildren(element('h3', title));
      const form = element('form');
      const save = element('button', 'Save changes', 'button-primary');
      save.type = 'submit';
      save.disabled = !canWrite || !runtime.writable;
      editor.append(form);
      return { form, save, key: formDraftKey(kind, id) };
    }

    function finishForm(form: HTMLFormElement, save: HTMLButtonElement, key: string, revision: number, submit: () => Promise<void>, onSaved?: () => void): void {
      rememberDraft(form, key, revision);
      const controls = element('div', undefined, 'form-actions');
      controls.append(save, button('Close editor (keep draft)', () => { editor.hidden = true; }));
      form.append(controls);
      editor.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
      form.addEventListener('submit', async event => {
        event.preventDefault();
        const controls = Array.from(form.querySelectorAll<Control | HTMLButtonElement>('input,select,textarea,button'));
        const disabled = controls.map(control => control.disabled);
        controls.forEach(control => { control.disabled = true; });
        let submittedDraft: string | null | undefined;
        try { submittedDraft = sessionStorage.getItem(key); }
        catch { message('The browser cannot read its draft storage. Keep this tab open until saving completes.', true); }
        try {
          await submit();
          let draftWarning = '';
          try {
            if (submittedDraft !== undefined && sessionStorage.getItem(key) === submittedDraft) {
              sessionStorage.removeItem(key);
              onSaved?.();
            }
            else draftWarning = ' A newer draft was retained in this tab; review it against the latest saved revision.';
          }
          catch { draftWarning = ' The browser could not clear its old draft; close this tab when finished.'; }
          if (editor.contains(form)) editor.hidden = true;
          try {
            if (!current()) return;
            await refreshData();
            message(`Changes saved. Public pages refresh automatically; updates normally appear within 15 seconds.${draftWarning}`, !!draftWarning);
          } catch {
            message(`Your changes were saved, but the workspace could not refresh. Refresh it before editing again.${draftWarning}`, true);
          }
        } catch (error) {
          message(error instanceof Error ? error.message : 'Unable to save changes. Your form has been preserved.', true);
        } finally {
          controls.forEach((control, index) => { control.disabled = disabled[index]; });
          save.disabled = !canWrite || !runtime.writable;
        }
      });
    }

    function openIncident(incident?: Incident, seed?: EvidenceDraft, draftId?: string): void {
      const { form, save, key } = openForm(incident ? `Update incident #${incident.id}` : 'Create an incident or maintenance window',
        draftId ? `incident-evidence:${draftId}` : 'incident', incident?.id);
      const privateContext = seed?.context || incident?.evidence?.context;
      if (privateContext) {
        const context = element('aside', undefined, 'evidence-context');
        context.append(element('h4', 'Source evidence (private)'), element('pre', privateContext),
          element('p', 'Review the affected services and public wording below. Source details are not included in the incident update. Nothing is published until you select Publish and save.'));
        form.append(context);
      }
      const title = element('input');
      title.required = true;
      title.maxLength = 200;
      title.value = incident?.title || seed?.title || '';
      field(form, 'Title', title, 'title');
      const components = element('select');
      components.multiple = components.required = true;
      components.size = Math.min(7, flatten(status.components).length);
      for (const component of flatten(status.components)) {
        const item = option(component.id, component.name);
        item.selected = (incident?.components || seed?.components || []).includes(component.id);
        components.append(item);
      }
      field(form, 'Affected services (hold Ctrl or Command to select several)', components, 'components');
      const severity = element('select');
      severity.append(...['degraded', 'outage', 'maintenance'].map(value => option(value)));
      severity.value = incident?.severity || seed?.severity || 'degraded';
      field(form, 'Impact / type', severity, 'severity');
      const state = element('select');
      state.append(...['investigating', 'identified', 'monitoring', 'resolved'].map(value => option(value)));
      state.value = incident?.state || seed?.state || 'investigating';
      field(form, 'Current state', state, 'state');
      const update = element('textarea');
      update.required = true;
      update.maxLength = 20000;
      update.value = seed?.message || '';
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
      form.append(element('p', 'Incidents affecting shared services, including GeoLocation Website, also appear on the other status site.', 'muted'));
      const preview = element('article', undefined, 'incident-preview');
      const updatePreview = (): void => {
        preview.replaceChildren(element('h4', title.value || 'Incident preview'), element('p', update.value || 'Your public update will appear here.'));
      };
      form.append(element('p', 'Public preview', 'eyebrow'), preview);
      finishForm(form, save, key, incident?.revision || 0, async () => {
        await request(`/api/ops/incidents${incident ? `/${incident.id}` : ''}`, {
          method: incident ? 'PUT' : 'POST',
          body: JSON.stringify({
            title: title.value, components: Array.from(components.selectedOptions).map(item => item.value),
            severity: severity.value, state: state.value, message: update.value, published: published.checked, revision: incident?.revision || 0,
            scheduledStart: severity.value === 'maintenance' && start.value ? new Date(start.value).toISOString() : null,
            scheduledEnd: severity.value === 'maintenance' && end.value ? new Date(end.value).toISOString() : null,
            evidence: seed ? { ...seed.evidence, context: seed.context } : undefined,
          }),
        });
      }, () => {
        if (draftId) {
          sessionStorage.removeItem(evidenceDraftPrefix + draftId);
          if (current() && new URLSearchParams(location.search).get('draft') === draftId)
            history.replaceState(null, '', '/ops/incidents');
        }
      });
      form.addEventListener('input', updatePreview);
      updatePreview();
      if (seed) form.dispatchEvent(new Event('input', { bubbles: true }));
    }

    function openBanner(banner?: Banner, archiving?: boolean): void {
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
      field(form, 'Show this notice on the public status page', published, 'published');
      form.append(element('p', `Shown on ${status.site.displayName}. Leave unchecked to keep a draft or withdraw the notice.`, 'muted'));
      const archived = element('input');
      archived.type = 'checkbox';
      archived.checked = archiving ?? banner?.archived ?? false;
      if (banner) field(form, 'Archive this notice (withdraw it and keep its record)', archived, 'archived');
      const updateArchive = (): void => {
        if (archived.checked) published.checked = false;
        published.disabled = archived.checked;
      };
      archived.addEventListener('change', updateArchive);
      updateArchive();
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
            archived: archived.checked,
          }),
        });
      });
      form.addEventListener('input', updatePreview);
      form.addEventListener('input', updateArchive);
      if (archiving !== undefined) {
        archived.checked = archiving;
        published.checked = false;
        form.dispatchEvent(new Event('input', { bubbles: true }));
      }
      updateArchive();
      updatePreview();
    }

    function renderEvidence(): void {
      evidenceSection.replaceChildren(element('h3', 'Evidence feed'),
        element('p', 'HTTP checks determine automatic availability. Azure alerts and advisories provide private context. Promote an item to prepare an incident; review it before publishing.', 'muted'));
      const cards = evidenceCards(evidence).filter(card => !card.site || card.site === status.site.id);
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
          const facts = element('dl', undefined, 'evidence-facts');
          for (const detail of card.details) facts.append(element('dt', detail.label), element('dd', detail.value));
          article.append(facts);
          if (card.href) article.append(link('Open source', card.href));
          const promote = button('Promote to incident', () => promoteEvidence(card));
          promote.disabled = !canWrite || !runtime.writable;
          article.append(promote);
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

    function promoteEvidence(card: EvidenceCard): void {
      const components = flatten(status.components);
      const ids = affectedComponents(card, components.map(component => component.id), runtime.workloads, health);
      const seed = incidentSeed(card, ids, components.filter(component => ids.includes(component.id)).map(component => component.name));
      const id = crypto.randomUUID();
      try {
        sessionStorage.setItem(evidenceDraftPrefix + id, JSON.stringify(seed));
      } catch {
        message('This browser could not save the evidence draft. Enable session storage or create an incident manually; nothing was published.', true);
        return;
      }
      navigateTo(`/ops/incidents?draft=${id}`);
    }

    function renderEvidenceDrafts(): void {
      draftsSection.replaceChildren(element('h3', 'Unsubmitted evidence drafts'),
        element('p', 'These drafts are saved in this browser tab. Resume one to review, edit or save it.', 'muted'));
      let count = 0;
      try {
        for (let index = 0; index < sessionStorage.length; index++) {
          const key = sessionStorage.key(index);
          if (!key?.startsWith(evidenceDraftPrefix)) continue;
          count++;
          const id = key.slice(evidenceDraftPrefix.length);
          try {
            const seed = readEvidenceDraft(sessionStorage.getItem(key) || '');
            const saved: { values?: { title?: unknown } } = JSON.parse(sessionStorage.getItem(formDraftKey(`incident-evidence:${id}`)) || '{}');
            const title = typeof saved.values?.title === 'string' ? saved.values.title : seed.title;
            const row = element('article', undefined, 'management-row');
            row.append(element('h4', title), link('Resume draft', `/ops/incidents?draft=${encodeURIComponent(id)}`));
            draftsSection.append(row);
          } catch {
            draftsSection.append(element('p', 'An evidence draft could not be read. Return to its monitoring source to prepare it again.', 'error'));
          }
        }
      } catch {
        message('This browser cannot list saved evidence drafts. Keep the original draft tab open or create an incident manually.', true);
      }
      draftsSection.hidden = count === 0;
    }

    function renderHealth(): void {
      healthSection.replaceChildren(element('h3', 'Service health'),
        element('p', 'The status shown publicly, the check behind it, and any active incident or maintenance override.', 'muted'));
      for (const check of health) {
        const row = element('article', undefined, 'health-check');
        row.append(element('h4', `${check.name}: ${check.status}`), element('p', check.source));
        if (check.target) row.append(link(check.target, check.target));
        if (check.probe) {
          const probe = check.probe;
          row.append(element('p', `${probe.success ? 'Passed' : 'Failed'} / ${probe.httpStatus === null ? 'No HTTP response' : `HTTP ${probe.httpStatus}`} / ${probe.durationMilliseconds} ms / ${formatDateTime(probe.observedAt)}`),
            element('p', `Observer: ${probe.observer}. ${probe.error || ''}`, 'muted'));
          if (!check.matchesPublication) row.append(element('p', 'This check does not match the displayed publication yet. Compare timestamps; publication or replication may still be catching up.', 'stale-warning'));
          if (Date.now() - new Date(probe.observedAt).getTime() > check.expectedIntervalSeconds * 3000)
            row.append(element('p', 'This check is stale. Do not treat its last successful result as current availability.', 'stale-warning'));
        } else {
          row.append(element('p', check.target ? 'No check result is available yet.' : 'No public HTTP check is configured. Azure evidence can be reviewed and promoted to an incident; absence of an alert does not prove availability.', 'muted'));
        }
        for (const incident of check.incidents)
          row.append(element('p', `Incident #${incident.id}: ${incident.title} (${incident.severity}, ${incident.state})`));
        for (const maintenance of check.maintenance) row.append(element('p', `Maintenance #${maintenance.id}: ${maintenance.title}`));
        if (check.lastFailure && check.lastFailure.observedAt !== check.probe?.observedAt)
          row.append(element('p', `Last failed check: ${formatDateTime(check.lastFailure.observedAt)} / ${check.lastFailure.error || 'No HTTP response'}`));
        if (check.probe?.success && check.incidents.length)
          row.append(element('p', 'The HTTP check passed; the active incident still affects the public status.'));
        healthSection.append(row);
      }
      if (!health.length) healthSection.append(element('p', 'No service diagnostics are available.', 'empty-state'));
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
      incidentSection.replaceChildren(element('h3', 'Incidents & maintenance'));
      const sourceUrl = `https://github.com/${runtime.github.repository}`;
      const sourceNote = element('p', undefined, 'muted');
      sourceNote.append('GitHub-managed incidents are edited in ', link(runtime.github.repository, sourceUrl),
        '. A trusted author, matching workload labels and status:published are required.',
        runtime.github.enabled ? ` Synced every ${runtime.github.pollSeconds / 60} minutes.` : ' GitHub ingestion is disabled on this origin.');
      incidentSection.append(sourceNote, link('Declare an incident in GitHub', `${sourceUrl}/issues/new/choose`));
      if (!incidents.length) incidentSection.append(element('p', 'No incidents or maintenance windows yet.', 'empty-state'));
      for (const incident of incidents) {
        const row = element('article', undefined, 'management-row');
        const copy = element('div');
        copy.append(element('h4', incident.title), element('p', `${incident.state} / ${incident.severity} / ${incident.published ? 'public' : 'draft'}`, 'muted'));
        copy.append(element('p', `Affected services: ${flatten(status.components).filter(component => incident.components.includes(component.id)).map(component => component.name).join(', ')}`));
        const sourceSite = incident.siteId && incident.siteId !== status.site.id ? incident.siteId : null;
        let control: HTMLAnchorElement | HTMLButtonElement;
        if (incident.sourceUrl) control = link('Manage in GitHub', incident.sourceUrl);
        else if (sourceSite) control = link('Manage on the originating status site',
          `${sourceSite === 'mx' ? 'https://mxstatus.io' : 'https://status.xtremeidiots.com'}/ops/incidents`);
        else control = button('Edit incident', () => openIncident(incident));
        if (control instanceof HTMLButtonElement) control.disabled = !canWrite || !runtime.writable;
        row.append(copy, control);
        incidentSection.append(row);
      }
      bannerSection.replaceChildren(element('h3', 'Manual banners'),
        element('p', `Banners appear on ${status.site.displayName} only. You can publish immediately or set a start and expiry time.`, 'muted'));
      const archivedFilter = element('input');
      archivedFilter.type = 'checkbox';
      archivedFilter.checked = showArchived;
      archivedFilter.addEventListener('change', () => {
        showArchived = archivedFilter.checked;
        renderLists();
        bannerSection.querySelector<HTMLInputElement>('[name="showArchived"]')?.focus();
      });
      field(bannerSection, 'Show archived notices', archivedFilter, 'showArchived');
      if (!banners.length) bannerSection.append(element('p', 'No announcements. Create a banner for important notices that do not need an incident.', 'empty-state'));
      for (const banner of banners.filter(banner => showArchived || !banner.archived)) {
        const row = element('article', undefined, 'management-row');
        const copy = element('div');
        const phase = banner.archived ? 'archived' : !banner.published ? 'draft' : banner.expiresAt && new Date(banner.expiresAt) <= new Date() ? 'expired'
          : banner.startsAt && new Date(banner.startsAt) > new Date() ? 'scheduled' : 'public';
        copy.append(element('h4', banner.title), element('p', `${phase} / ${banner.tone}${banner.expiresAt ? ` / expires ${formatDateTime(banner.expiresAt)}` : ''}`, 'muted'));
        const edit = button('Edit banner', () => openBanner(banner));
        edit.disabled = !canWrite || !runtime.writable;
        const archive = button(banner.archived ? 'Restore notice as draft' : 'Archive notice', () => openBanner(banner, !banner.archived));
        archive.disabled = !canWrite || !runtime.writable;
        row.append(copy, edit, archive);
        bannerSection.append(row);
      }
      message(`${status.site.displayName}. ${runtime.writable ? 'Writer connected.' : 'Writer unavailable; publishing is read-only.'} ${canWrite ? 'You can publish public communications.' : 'Your access is read-only.'}`);
      if (page === 'evidence') renderEvidence();
      if (page === 'overview' || page === 'evidence') renderHealth();
      if (page === 'incidents') renderEvidenceDrafts();
    }

    function interactingWithWorkspace(): boolean {
      return !editor.hidden || incidentSection.contains(document.activeElement) || bannerSection.contains(document.activeElement);
    }

    async function refreshData(automatic = false): Promise<void> {
      refresh.disabled = true;
      try {
        const responses = await Promise.all([
          request('/api/ops/incidents'), request('/api/ops/banners'), request('/api/ops/runtime'), request('/api/ops/evidence'), request('/api/ops/health'),
        ]);
        const latest = await Promise.all(responses.map(response => response.json()));
        if (automatic && interactingWithWorkspace()) return;
        [incidents, banners, runtime, evidence, health] = latest;
        if (current()) {
          renderLists();
          await refreshTraffic?.();
        }
      } catch (error) {
        message(error instanceof Error ? error.message : 'The workspace could not be refreshed.', true);
        throw error;
      } finally { refresh.disabled = false; }
    }
    renderLists();
    const autoRefresh = (): void => {
      if (current() && !interactingWithWorkspace() && !refresh.disabled && (page === 'incidents' || page === 'banners'))
        void refreshData(true).catch(error => message(error instanceof Error ? error.message : 'Automatic refresh failed.', true));
    };
    root.addEventListener('status:refresh-workspace', autoRefresh);
    removeWorkspaceRefresh = () => root.removeEventListener('status:refresh-workspace', autoRefresh);
    if (page === 'traffic') {
      const trafficSection = element('section', undefined, 'panel');
      root.append(trafficSection);
      refreshTraffic = await renderTraffic(trafficSection, request,
        canWrite && Array.isArray(roles) && roles.includes('Status.TrafficOperator'), () => runtime.writable, current);
    }
    const draftId = new URLSearchParams(location.search).get('draft');
    if (page === 'incidents' && draftId) {
      try {
        const saved = sessionStorage.getItem(evidenceDraftPrefix + draftId);
        if (!saved) throw new Error('This evidence draft is no longer available in this tab. Return to monitoring evidence to prepare it again.');
        const seed = readEvidenceDraft(saved);
        openIncident(undefined, seed, draftId);
      } catch (error) {
        message(error instanceof Error ? error.message : 'The evidence draft could not be opened.', true);
      }
    }
  } catch (error) {
    if (!current()) return;
    message(error instanceof Error ? error.message : 'Operations are unavailable. Public status remains available.', true);
    root.append(button('Try again', () => { void renderOperations(navigateTo); }));
  }
}
