export type Control = HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement;

export function element<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string, className?: string): HTMLElementTagNameMap[K] {
  const result = document.createElement(tag);
  if (text !== undefined) result.textContent = text;
  if (className) result.className = className;
  return result;
}

export function button(text: string, action: () => void, primary = false): HTMLButtonElement {
  const result = element('button', text, primary ? 'button-primary' : '');
  result.type = 'button';
  result.addEventListener('click', action);
  return result;
}

export function link(text: string, href: string): HTMLAnchorElement {
  const result = element('a', text);
  result.href = href;
  return result;
}

export function option(value: string, label = value): HTMLOptionElement {
  const result = element('option', label);
  result.value = value;
  return result;
}

export function field(form: HTMLElement, label: string, control: Control, name: string): void {
  control.name = name;
  const wrapper = element('label', label, control instanceof HTMLInputElement && control.type === 'checkbox' ? 'checkbox-field' : '');
  wrapper.append(control);
  form.append(wrapper);
}
