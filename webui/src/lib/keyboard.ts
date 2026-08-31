/** Shared keyboard-shortcut guards. Keep text-editing semantics native while allowing
 * view-level navigation to continue from non-text controls such as checkboxes. */
export function keyboardTargetIsEditing(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) return false;
  return Boolean(target.closest([
    'textarea',
    'select',
    '[contenteditable="true"]',
    '[contenteditable="plaintext-only"]',
    '[role="textbox"]',
    'input:not([type="checkbox"]):not([type="radio"]):not([type="button"]):not([type="submit"]):not([type="reset"])',
  ].join(', ')));
}

/** Controls whose Enter/Space activation should win over an application shortcut. */
export function keyboardTargetUsesNativeActivation(target: EventTarget | null, key?: string): boolean {
  if (!(target instanceof Element)) return false;
  // Enter does not natively toggle checkboxes, so a focused result checkbox should
  // not suppress view/application-level Enter shortcuts. Space still owns checkbox
  // activation through the default path used by callers that omit `key`.
  if (key === 'Enter' && target.closest('input[type="checkbox"]')) return false;
  return Boolean(target.closest('button, a[href], input, select, textarea, [contenteditable="true"], [contenteditable="plaintext-only"], [role="button"], [role="menuitem"], [role="option"], [role="tab"]'));
}

export function keyboardShortcutHasModifier(event: KeyboardEvent): boolean {
  return event.metaKey || event.ctrlKey || event.altKey;
}

/** Modal dialogs and open top-layer popovers temporarily own the keyboard. */
export function blockingKeyboardSurfaceOpen(): boolean {
  if (typeof document === 'undefined') return false;
  if (document.querySelector('[role="dialog"][aria-modal="true"]')) return true;
  try {
    return Boolean(document.querySelector('[popover]:popover-open'));
  } catch {
    return false;
  }
}

export function focusFirstKeyboardItemControl(container: HTMLElement | null): boolean {
  if (!container) return false;
  const target = container.querySelector<HTMLElement>([
    'button:not([disabled])',
    'input:not([disabled])',
    'select:not([disabled])',
    'textarea:not([disabled])',
    'a[href]',
    '[tabindex]:not([tabindex="-1"])',
  ].join(', '));
  if (!target) return false;
  target.focus();
  return true;
}

export function focusKeyboardItem(target: HTMLElement | null, options: { revealViewStart?: boolean } = {}): void {
  if (!target || typeof window === 'undefined') return;
  window.requestAnimationFrame(() => {
    target.focus({ preventScroll: true });
    if (options.revealViewStart) {
      const scrollHost = target.closest<HTMLElement>('.page-content');
      if (scrollHost) scrollHost.scrollTo({ top: 0, behavior: 'auto' });
      else window.scrollTo({ top: 0, behavior: 'auto' });
      return;
    }
    target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  });
}
