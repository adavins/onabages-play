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
