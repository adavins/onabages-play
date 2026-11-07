// @ts-check
// Avís breu que apareix a sobre d'un element i desapareix sol al cap d'uns segons (fase 5, pas 22).
// No cal cap biblioteca: és un <div> amb icona + text que es treu amb un temporitzador.
// Si en surt un altre al mateix lloc, substitueix l'anterior (no se n'apilen).

/** Quant es veu l'avís (ms), si no es diu una altra cosa. */
const TOAST_MS = 6000;

/** Icona de cada tipus d'avís (color + icona + text, mai només color). */
const ICONS = { warning: 'bi-exclamation-triangle-fill', info: 'bi-check-circle-fill' };

/**
 * Mostra un avís a sobre de `host` (que ha de tenir `position: relative`).
 * Es tanca sol, o en fer-hi clic.
 * @param {HTMLElement} host
 * @param {string} text
 * @param {{ kind?: 'warning' | 'info', icon?: string, ms?: number }} [options]
 *   kind: groc d'advertència (per defecte) o blau informatiu (p. ex. confirmar una acció); icon: una altra icona.
 */
export function showToast(host, text, { kind = 'warning', icon, ms = TOAST_MS } = {}) {
  host.querySelector(':scope > .obp-toast')?.remove();

  const toast = document.createElement('div');
  toast.className = `obp-toast obp-toast-${kind}`;
  toast.setAttribute('role', 'status');
  toast.title = 'Clica per tancar';
  toast.innerHTML = `<i class="bi ${icon ?? ICONS[kind]}" aria-hidden="true"></i><span></span>`;
  /** @type {HTMLElement} */ (toast.querySelector('span')).textContent = text;
  toast.addEventListener('click', () => toast.remove());
  host.append(toast);

  // Primer s'esvaeix i després es treu.
  window.setTimeout(() => toast.classList.add('obp-toast-hiding'), ms);
  window.setTimeout(() => toast.remove(), ms + 400);
}
