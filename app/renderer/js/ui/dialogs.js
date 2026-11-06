// @ts-check
// Diàlegs de confirmació, amb l'element <dialog> del navegador i estils de Bootstrap.
//
// Ús:
//   const choice = await choose({
//     title: 'Tancar l\'aplicació',
//     message: 'Hi ha enregistraments en curs.',
//     buttons: [
//       { label: 'Cancel·lar', value: 'cancel' },
//       { label: 'Aturar-ho tot', value: 'stop', style: 'danger' },
//     ],
//   });
// Retorna el `value` del botó premut, o el primer botó si es tanca amb Esc.

import { escapeHtml } from './format.js';

/**
 * @typedef {{ label: string, value: string, style?: string, icon?: string }} DialogButton
 * @typedef {{ title: string, message: string, icon?: string, buttons: DialogButton[] }} DialogOptions
 */

/**
 * Finestra d'informació: una taula d'etiquetes i valors, amb un botó per tancar-la.
 * Les files sense valor no es mostren.
 * @param {string} title
 * @param {[string, unknown][]} rows
 */
export function showInfo(title, rows) {
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog obp-info';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-info-circle"></i> ${escapeHtml(title)}</h2>
      <dl class="obp-info-list">
        ${rows.filter(([, value]) => value !== null && value !== undefined && value !== '')
          .map(([label, value]) => `<dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd>`).join('')}
      </dl>
      <div class="obp-dialog-buttons">
        <button class="btn btn-primary" value="ok">Tancar</button>
      </div>
    </form>`;
  document.body.appendChild(dialog);
  dialog.showModal();
  dialog.addEventListener('close', () => dialog.remove());
}

/** @param {DialogOptions} options @returns {Promise<string>} */
export function choose(options) {
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog';
  const icon = options.icon ? `<i class="bi ${options.icon}"></i> ` : '';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title">${icon}${escapeHtml(options.title)}</h2>
      <div class="obp-dialog-message">${escapeHtml(options.message).replaceAll('\n', '<br>')}</div>
      <div class="obp-dialog-buttons">
        ${options.buttons.map((b) => `
          <button class="btn btn-${b.style ?? 'secondary'}" value="${escapeHtml(b.value)}">
            ${b.icon ? `<i class="bi ${b.icon}"></i> ` : ''}${escapeHtml(b.label)}
          </button>`).join('')}
      </div>
    </form>`;

  document.body.appendChild(dialog);
  dialog.showModal();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      // Esc tanca sense valor: es pren el primer botó (el més prudent).
      resolve(dialog.returnValue || options.buttons[0].value);
      dialog.remove();
    });
  });
}

/**
 * Demana un text (p. ex. el nom d'un perfil). Retorna el text escrit, o null si es cancel·la.
 * @param {{ title: string, label: string, value?: string, okLabel: string, icon?: string, maxLength?: number }} options
 * @returns {Promise<string | null>}
 */
export function askText(options) {
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog';
  const icon = options.icon ? `<i class="bi ${options.icon}"></i> ` : '';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title">${icon}${escapeHtml(options.title)}</h2>
      <label class="form-label" for="obp-ask-text">${escapeHtml(options.label)}</label>
      <input id="obp-ask-text" class="form-control" type="text" autocomplete="off"
             maxlength="${options.maxLength ?? 60}" value="${escapeHtml(options.value ?? '')}">
      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary" value="cancel" formnovalidate>Cancel·lar</button>
        <button class="btn btn-primary js-ok" value="ok">${escapeHtml(options.okLabel)}</button>
      </div>
    </form>`;

  document.body.appendChild(dialog);
  const input = /** @type {HTMLInputElement} */ (dialog.querySelector('input'));
  const ok = /** @type {HTMLButtonElement} */ (dialog.querySelector('.js-ok'));
  // Sense text no es pot acceptar.
  const update = () => { ok.disabled = input.value.trim() === ''; };
  input.addEventListener('input', update);
  // Retorn accepta (si no, el formulari faria servir el primer botó, que és Cancel·lar).
  input.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    if (!ok.disabled) dialog.close('ok');
  });
  update();
  dialog.showModal();
  input.select();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      resolve(dialog.returnValue === 'ok' && input.value.trim() !== '' ? input.value.trim() : null);
      dialog.remove();
    });
  });
}

/**
 * Demana el PIN d'un perfil (fase 5, pas 13) i el fa comprovar a `check` (que ho pregunta al motor).
 * Si no és correcte, ho diu i es pot tornar a provar. Retorna true si s'ha entrat el PIN correcte,
 * false si es cancel·la.
 * @param {{ title: string, message: string, check: (pin: string) => Promise<boolean> }} options
 * @returns {Promise<boolean>}
 */
export function askPin(options) {
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog obp-pin';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-lock-fill"></i> ${escapeHtml(options.title)}</h2>
      <label class="form-label" for="obp-ask-pin">${escapeHtml(options.message)}</label>
      <input id="obp-ask-pin" class="form-control obp-pin-input" type="password" inputmode="numeric"
             autocomplete="off" maxlength="8">
      <div class="error-text obp-pin-error js-error" role="alert"></div>
      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary" value="cancel" formnovalidate>Cancel·lar</button>
        <button class="btn btn-primary js-ok" type="button">D'acord</button>
      </div>
    </form>`;

  document.body.appendChild(dialog);
  const input = /** @type {HTMLInputElement} */ (dialog.querySelector('input'));
  const ok = /** @type {HTMLButtonElement} */ (dialog.querySelector('.js-ok'));
  const error = /** @type {HTMLElement} */ (dialog.querySelector('.js-error'));
  let valid = false;

  const submit = async () => {
    if (ok.disabled || input.value === '') return;
    ok.disabled = true;
    try {
      valid = await options.check(input.value);
    } catch (err) {
      error.textContent = /** @type {Error} */ (err).message;
      ok.disabled = false;
      return;
    }
    if (valid) {
      dialog.close('ok');
      return;
    }
    error.textContent = 'PIN incorrecte. Torna-ho a provar.';
    ok.disabled = false;
    input.select();
  };

  ok.addEventListener('click', submit);
  input.addEventListener('input', () => { error.textContent = ''; });
  // Retorn accepta (si no, el formulari faria servir el primer botó, que és Cancel·lar).
  input.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    submit();
  });
  dialog.showModal();
  input.focus();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      resolve(valid);
      dialog.remove();
    });
  });
}

/**
 * Fa triar un element d'una llista (un desplegable). Retorna el `value` triat, o null si es cancel·la.
 * @param {{ title: string, label: string, message?: string, okLabel: string, okStyle?: string, icon?: string,
 *           options: { value: string, label: string }[] }} options
 * @returns {Promise<string | null>}
 */
export function pickOne(options) {
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog';
  const icon = options.icon ? `<i class="bi ${options.icon}"></i> ` : '';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title">${icon}${escapeHtml(options.title)}</h2>
      <label class="form-label" for="obp-pick-one">${escapeHtml(options.label)}</label>
      <select id="obp-pick-one" class="form-select">
        ${options.options.map((o) => `<option value="${escapeHtml(o.value)}">${escapeHtml(o.label)}</option>`).join('')}
      </select>
      ${options.message ? `<div class="obp-dialog-message mt-3">${escapeHtml(options.message).replaceAll('\n', '<br>')}</div>` : ''}
      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary" value="cancel">Cancel·lar</button>
        <button class="btn btn-${options.okStyle ?? 'primary'}" value="ok">${escapeHtml(options.okLabel)}</button>
      </div>
    </form>`;

  document.body.appendChild(dialog);
  const select = /** @type {HTMLSelectElement} */ (dialog.querySelector('select'));
  dialog.showModal();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      resolve(dialog.returnValue === 'ok' ? select.value : null);
      dialog.remove();
    });
  });
}
