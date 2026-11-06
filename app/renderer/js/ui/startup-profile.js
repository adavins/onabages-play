// @ts-check
// Finestra inicial per triar el perfil (fase 5, pas 13b; D-096).
//
// Surt només quan el motor s'acaba d'arrencar en obrir l'aplicació (Electron ho diu una sola vegada)
// i no sona ni s'enregistra res. Amb un sol perfil no surt. Un botó per perfil (el de l'última vegada,
// marcat i amb el focus: Retorn hi entra) i «Perfil nou…». Un perfil amb PIN demana el PIN.
// No es pot tancar sense triar (amb Esc torna a sortir): si no, es podria entrar sense el PIN.

import { askPin, askText } from './dialogs.js';
import { escapeHtml } from './format.js';

/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */
/** @typedef {{ id: string, name: string, active: boolean, hasPin: boolean }} Profile */

/**
 * @param {{ request: Request, busy: { playing: string[], recording: string[] },
 *           onSwitch: (profile: { name: string }) => Promise<void> }} options
 *   onSwitch: el motor s'està aturant per canviar de perfil; cal tornar-lo a arrencar
 */
export async function chooseStartupProfile({ request, busy, onSwitch }) {
  // Si alguna cosa sona o enregistra, no s'interromp res.
  if (busy.playing.length > 0 || busy.recording.length > 0) return;

  /** @type {Profile[]} */
  let profiles;
  try {
    profiles = (await request('profile.list')).profiles;
  } catch {
    return; // sense llista, es continua amb el perfil que ja hi ha
  }
  if (profiles.length <= 1) return;

  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog obp-startup';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-person-badge"></i> Tria el perfil</h2>
      <div class="obp-startup-grid">
        ${profiles.map((p) => `
          <button type="button" class="obp-startup-profile${p.active ? ' last' : ''}" data-id="${escapeHtml(p.id)}">
            <span class="obp-startup-name">${escapeHtml(p.name)}</span>
            <span class="obp-startup-info">
              ${p.hasPin ? '<i class="bi bi-lock-fill"></i> Amb PIN' : ''}
              ${p.active ? `${p.hasPin ? ' · ' : ''}<i class="bi bi-clock-history"></i> L'última vegada` : ''}
            </span>
          </button>`).join('')}
      </div>
      <div class="error-text js-error" role="alert"></div>
      <div class="obp-dialog-buttons">
        <button type="button" class="btn btn-outline-secondary js-new"><i class="bi bi-plus-lg"></i> Perfil nou…</button>
      </div>
    </form>`;

  const error = /** @type {HTMLElement} */ (dialog.querySelector('.js-error'));
  // Mentre es comprova un PIN o es canvia de perfil, els botons no fan res.
  let working = false;
  // Només es tanca de debò quan s'ha triat un perfil (vegeu més avall, Esc).
  let chosen = false;
  const finish = () => {
    chosen = true;
    dialog.close();
  };

  /** @param {Profile} profile */
  const choose = async (profile) => {
    if (profile.hasPin) {
      const ok = await askPin({
        title: profile.name,
        message: 'Escriu el PIN d\'aquest perfil:',
        check: async (pin) => (await request('profile.checkPin', { profile: profile.id, pin })).valid,
      });
      if (!ok) return;
    }
    if (profile.active) {
      finish();
      return;
    }
    await request('profile.switch', { profile: profile.id });
    finish();
    await onSwitch(profile);
  };

  const createNew = async () => {
    const name = await askText({
      title: 'Perfil nou',
      icon: 'bi-plus-lg',
      label: 'Nom del perfil (p. ex. el del programa)',
      okLabel: 'Crear i entrar-hi',
    });
    if (name === null) return;
    const created = (await request('profile.create', { name })).profile;
    await request('profile.switch', { profile: created.id });
    finish();
    await onSwitch(created);
  };

  /** @param {() => Promise<void>} action */
  const run = async (action) => {
    if (working) return;
    working = true;
    error.textContent = '';
    try {
      await action();
    } catch (err) {
      error.textContent = /** @type {Error} */ (err).message;
    } finally {
      working = false;
    }
  };

  dialog.addEventListener('click', (e) => {
    const target = /** @type {HTMLElement} */ (e.target);
    const button = /** @type {HTMLElement | null} */ (target.closest('.obp-startup-profile'));
    if (button) {
      const profile = profiles.find((p) => p.id === button.dataset.id);
      if (profile) run(() => choose(profile));
    } else if (target.closest('.js-new')) {
      run(createNew);
    }
  });
  // Esc no tanca: s'ha de triar un perfil (amb el seu PIN, si en té). El navegador deixa tancar un diàleg
  // amb Esc encara que s'impedeixi si no s'hi ha fet cap clic; per això, si es tanca sense triar, es torna a obrir.
  dialog.addEventListener('cancel', (e) => e.preventDefault());
  dialog.addEventListener('close', () => { if (!chosen) dialog.showModal(); });

  document.body.appendChild(dialog);
  dialog.showModal();
  /** @type {HTMLElement | null} */ (dialog.querySelector('.obp-startup-profile.last'))?.focus();

  await new Promise((resolve) => dialog.addEventListener('close', () => { if (chosen) resolve(undefined); }));
  dialog.remove();
}
