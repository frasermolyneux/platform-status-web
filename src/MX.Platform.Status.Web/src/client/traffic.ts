import { button, element, field, link, option } from './ops-ui';
import { formatDateTime } from './time';

interface TrafficRow {
  hostname: string;
  mode: string;
  state: string;
  verified: boolean;
  routeId: string | null;
  routeFingerprint: string | null;
  revision: number;
  statusUrl: string;
  httpStatus: number | null;
  error: string | null;
  lastOperation: { actor: string; reason: string; updatedAt: string; phase: string; mode: string } | null;
}

interface TrafficWorkspace {
  enabled: boolean;
  hosts: TrafficRow[];
  quota: { requests: number | null; freeDailyAllowance: number; warning: boolean; observedAt: string; error: string | null } | null;
}

export async function renderTraffic(root: HTMLElement, request: (path: string, init?: RequestInit) => Promise<Response>,
  canControl: boolean, writable: () => boolean, current: () => boolean): Promise<() => Promise<void>> {
  const notice = element('p', 'Loading live Cloudflare traffic controls...', 'operator-notice');
  notice.setAttribute('role', 'status');
  const list = element('div', undefined, 'traffic-hosts');
  const editor = element('section', undefined, 'editor-panel');
  editor.hidden = true;
  const quota = element('p', undefined, 'operator-notice');
  const writerNotice = element('p', 'The audit writer is unavailable. Changes are disabled; use the documented Cloudflare dashboard override for an emergency.', 'operator-notice error');
  let changing = false;
  let readGeneration = 0;
  let workspace: TrafficWorkspace;
  const refresh = button('Refresh traffic controls', () => {
    void load().catch(showError);
  });
  root.replaceChildren(element('h3', 'Hostname traffic controls'),
    element('p', 'Maintenance is enforced at Cloudflare, independently of the website. Each hostname is controlled separately. Status sites are excluded.', 'muted'),
    element('p', 'The edge page uses a generic maintenance message. Publish custom wording separately as a banner or incident; these actions are not an atomic operation.'),
    link('Manage public announcements', '/ops/banners'), document.createTextNode(' / '),
    link('Manage incidents and maintenance', '/ops/incidents'), quota, refresh, notice, editor, list);
  if (!canControl) root.insertBefore(element('p', 'Changing traffic requires both Status.Operator and the separately assigned Status.TrafficOperator role. Sign in again after a role assignment.', 'operator-notice'), refresh);
  root.insertBefore(writerNotice, refresh);
  writerNotice.hidden = writable();

  function showError(error: unknown): void {
    if (!current()) return;
    notice.className = 'operator-notice error';
    notice.textContent = error instanceof Error ? error.message : 'Traffic controls are unavailable. No automatic traffic change was made.';
  }

  function render(): void {
    writerNotice.hidden = writable();
    list.replaceChildren();
    if (!workspace.enabled) {
      notice.textContent = 'Traffic controls are not configured on this deployment.';
      return;
    }
    const usage = workspace.quota;
    quota.className = `operator-notice${usage?.warning || usage?.error ? ' error' : ''}`;
    quota.textContent = usage?.requests !== null && usage?.requests !== undefined
      ? `Workers account usage today (UTC, estimated): ${usage.requests.toLocaleString()} / ${usage.freeDailyAllowance.toLocaleString()} free daily requests. ${usage.warning ? 'Usage is approaching or exceeding the free allowance. ' : ''}The allowance is shared across all Workers. Maintenance routes fail closed at the quota.`
      : usage?.error || 'Account usage is unavailable. Workers Free has a shared allowance of 100,000 requests/day; exceeding it must not reopen a site.';
    for (const host of workspace.hosts) {
      const row = element('article', undefined, 'management-row');
      row.dataset.hostname = host.hostname;
      const copy = element('div');
      copy.append(element('h4', host.hostname),
        element('p', `${host.state} / ${host.mode}${host.httpStatus ? ` / HTTP ${host.httpStatus}` : ''}`, 'traffic-state'));
      if (host.error) copy.append(element('p', host.error, 'operator-notice error'));
      if (host.lastOperation) {
        const audit = host.lastOperation;
        copy.append(element('p', `Last request: ${audit.phase} / ${audit.mode} / ${formatDateTime(audit.updatedAt)} / actor ${audit.actor}`, 'muted'),
          element('p', `Private reason: ${audit.reason}`));
      }
      const actions = element('div', undefined, 'operator-actions');
      const enable = button(host.state === 'uncertain' ? 'Reconcile previous request'
        : host.mode === 'normal' ? 'Enable maintenance' : 'Change maintenance', () => open(host, false));
      enable.disabled = !canControl || !writable() || changing || host.mode === 'conflict';
      enable.setAttribute('aria-label', `${enable.textContent} for ${host.hostname}`);
      actions.append(enable);
      if (host.mode !== 'normal' && host.mode !== 'conflict' && host.state !== 'uncertain') {
        const restore = button('Restore traffic', () => open(host, true));
        restore.disabled = !canControl || !writable() || changing;
        restore.setAttribute('aria-label', `Restore traffic for ${host.hostname}`);
        actions.append(restore);
      }
      row.append(copy, actions);
      list.append(row);
    }
  }

  async function load(): Promise<void> {
    const generation = ++readGeneration;
    refresh.disabled = true;
    try {
      const response = await request('/api/ops/traffic');
      const next: TrafficWorkspace = await response.json();
      if (!current() || generation !== readGeneration) return;
      workspace = next;
      render();
    } catch (error) {
      if (generation === readGeneration) throw error;
    } finally { if (generation === readGeneration) refresh.disabled = changing; }
  }

  function open(host: TrafficRow, restoring: boolean): void {
    if (changing) return;
    const reconciling = host.state === 'uncertain';
    const reconcileMode = reconciling ? host.lastOperation?.mode : null;
    if (reconciling && !reconcileMode) {
      showError(new Error('The previous requested mode is unavailable. Inspect Cloudflare before taking further action.'));
      return;
    }
    const form = element('form', undefined, 'operator-form');
    const mode = element('select');
    mode.append(...(reconciling && reconcileMode ? [option(reconcileMode, `Reconcile previous ${reconcileMode} request`)]
      : restoring ? [option('normal', 'Restore normal traffic')]
      : [option('page', '503 maintenance page'), option('redirect', 'Temporary browser redirect to status')]));
    if (!reconciling && !restoring && host.mode === 'redirect') mode.value = 'redirect';
    const reason = element('textarea');
    reason.required = true;
    reason.maxLength = 500;
    const confirmation = element('input');
    confirmation.required = true;
    confirmation.placeholder = host.hostname;
    confirmation.autocomplete = 'off';
    field(form, 'Behaviour', mode, 'mode');
    field(form, 'Private audit reason (not published)', reason, 'reason');
    field(form, `Type ${host.hostname} to confirm`, confirmation, 'confirmation');
    const preview = element('p', reconciling
      ? 'Only checks whether the previous requested outcome is now confirmed. It sends no additional edge mutation. Other changes remain blocked until reconciliation succeeds; inspect Cloudflare if the outcome remains unknown.'
      : restoring
      ? 'This removes only this hostname\'s maintenance route. Confirm the application is ready before restoring traffic. Public announcements are not automatically withdrawn.'
      : 'New requests will not reach the application. Browser redirects do not forward paths, queries, form submissions or game downloads; non-browser requests receive 503. Maintenance has no automatic expiry.');
    const target = link('View the public status destination', host.statusUrl);
    target.target = '_blank';
    target.rel = 'noopener noreferrer';
    const submit = element('button', reconciling ? 'Confirm reconciliation' : restoring ? 'Confirm restore traffic' : 'Confirm maintenance', 'button-primary');
    submit.type = 'submit';
    form.append(preview, target, submit, button('Cancel', () => { editor.hidden = true; }));
    form.addEventListener('submit', event => {
      event.preventDefault();
      if (confirmation.value !== host.hostname) {
        showError(new Error('Type the exact hostname to confirm. No traffic change was made.'));
        return;
      }
      if (changing) return;
      changing = true;
      readGeneration++;
      let requiresReopen = false;
      refresh.disabled = true;
      render();
      const fields = Array.from(form.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement | HTMLButtonElement>('input,select,textarea,button'));
      const body = JSON.stringify({ mode: mode.value, reason: reason.value, confirmation: confirmation.value,
        revision: host.revision, routeId: host.routeId, routeFingerprint: host.routeFingerprint });
      fields.forEach(control => { control.disabled = true; });
      notice.className = 'operator-notice';
      notice.textContent = `${reconciling ? 'Reconciling previous request' : restoring ? 'Restoring traffic' : 'Enabling maintenance'} for ${host.hostname}. Waiting for Cloudflare and public verification...`;
      void (async () => {
        try {
          const response = await request(`/api/ops/traffic/${encodeURIComponent(host.hostname)}`, { method: 'POST', body });
          const updated: TrafficRow = await response.json();
          if (!current()) return;
          workspace.hosts = workspace.hosts.map(row => row.hostname === updated.hostname ? updated : row);
          editor.hidden = true;
          notice.textContent = `${host.hostname}: ${updated.state}. ${reconciling
            ? 'The previous request is reconciled. No additional edge mutation was sent.'
            : restoring ? 'The maintenance route is removed.' : 'The expected public maintenance response is confirmed.'} Public announcements are managed separately.`;
        } catch (error) {
          if (!current()) return;
          requiresReopen = true;
          try { await load(); } catch (refreshError) { showError(refreshError); }
          showError(new Error(`${error instanceof Error ? error.message : 'The change could not be confirmed.'} Reopen this hostname's controls before retrying; the submitted reason is retained below.`));
        } finally {
          changing = false;
          fields.forEach(control => { control.disabled = false; });
          submit.disabled = requiresReopen;
          refresh.disabled = false;
          if (current()) render();
        }
      })();
    });
    editor.replaceChildren(element('h3', `${reconciling ? 'Reconcile previous request' : restoring ? 'Restore traffic' : 'Configure maintenance'}: ${host.hostname}`), form);
    editor.hidden = false;
  }

  try {
    await load();
    if (current() && workspace!.enabled) notice.textContent = 'Live Cloudflare configuration loaded. No traffic is changed until you explicitly confirm an operation.';
  } catch (error) { showError(error); }
  return async () => {
    if (changing) { render(); return; }
    try { await load(); } catch (error) { showError(error); }
  };
}
