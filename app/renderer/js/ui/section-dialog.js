// @ts-check
// Diàleg per crear o editar un separador de secció (D-042): nom i color.
// Retorna { title, color } si es prem «Desar», o null si es cancel·la. color és null si no en té.

import { escapeHtml } from './format.js';

/** Colors possibles (els mateixos que accepta el motor) i el seu nom. Els tons són a css/app.css (--obp-section-*). */
export const SECTION_COLORS = [
  ['red', 'Vermell'],
  ['orange', 'Taronja'],
  ['yellow', 'Groc'],
  ['green', 'Verd'],
  ['teal', 'Turquesa'],
  ['blue', 'Blau'],
  ['purple', 'Lila'],
  ['pink', 'Rosa'],
];

/** Llargada màxima del nom (la mateixa que el motor). */
const MAX_TITLE = 80;

/**
 * @param {{ title?: string, color?: string | null }} [current]  la secció que s'edita (buit: una de nova)
 * @returns {Promise<{ title: string, color: string | null } | null>}
 */
export function editSection(current = {}) {
  const isNew = current.title === undefined;
  const selected = current.color ?? '';
  const swatch = (/** @type {string} */ value, /** @type {string} */ label) => `
    <label class="section-swatch section-color-${value || 'none'}" title="${label}">
      <input type="radio" name="sd-color" value="${value}"${value === selected ? ' checked' : ''} aria-label="${label}">
      <span>${value ? '' : '<i class="bi bi-slash-lg"></i>'}</span>
    </label>`;

  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog obp-form';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-bookmark-fill"></i> ${isNew ? 'Secció nova' : 'Editar la secció'}</h2>

      <label class="form-label" for="sd-title">Nom</label>
      <input id="sd-title" class="form-control mb-3" type="text" maxlength="${MAX_TITLE}" required
             value="${escapeHtml(current.title ?? '')}" placeholder="p. ex. ESPORTS">

      <label class="form-label">Color</label>
      <div class="section-swatches mb-3">
        ${swatch('', 'Sense color')}
        ${SECTION_COLORS.map(([value, label]) => swatch(value, label)).join('')}
      </div>

      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary js-cancel" type="button">Cancel·lar</button>
        <button class="btn btn-primary" value="save"><i class="bi bi-check-lg"></i> Desar</button>
      </div>
    </form>`;

  const title = /** @type {HTMLInputElement} */ (dialog.querySelector('#sd-title'));
  /** @type {HTMLElement} */ (dialog.querySelector('.js-cancel')).addEventListener('click', () => dialog.close('cancel'));

  document.body.appendChild(dialog);
  dialog.showModal();
  title.select();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      const color = /** @type {HTMLInputElement | null} */ (dialog.querySelector('input[name="sd-color"]:checked'))?.value;
      // Intro dins del nom també desa. Un nom buit no es pot desar (required).
      resolve(dialog.returnValue === 'save' && title.value.trim()
        ? { title: title.value.trim(), color: color || null }
        : null);
      dialog.remove();
    });
  });
}
