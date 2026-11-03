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
