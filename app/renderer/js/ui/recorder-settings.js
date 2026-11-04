// @ts-check
// Diàleg per canviar el nom i la carpeta de treball d'un enregistrador.
// Retorna { name, folder } si es prem «Desar», o null si es cancel·la.

import { escapeHtml } from './format.js';

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

/**
 * @param {any} recorder  l'objecte recorder del motor
 * @returns {Promise<{ name: string, folder: string } | null>}
 */
export function editRecorderSettings(recorder) {
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog obp-form';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-pencil"></i> Enregistrador ${escapeHtml(recorder.id)}</h2>

      <label class="form-label" for="rs-name">Nom</label>
      <input id="rs-name" class="form-control" type="text" maxlength="60"
             value="${escapeHtml(recorder.name)}" placeholder="Enregistrador ${escapeHtml(recorder.id)}">
      <div class="form-text mb-3">S'utilitza al nom dels fitxers: <span class="js-example"></span></div>

      <label class="form-label">Carpeta de treball</label>
      <div class="input-group mb-1">
        <input class="form-control js-folder" type="text" readonly value="${escapeHtml(recorder.folder)}" title="${escapeHtml(recorder.folder)}">
        <button class="btn btn-outline-secondary js-browse" type="button"><i class="bi bi-folder2-open"></i> Canviar…</button>
      </div>
      <div class="form-text mb-3">On es creen els fitxers mentre s'enregistra. Després, amb «Desar», es poden moure on calgui.</div>

      ${recorder.state === 'recording'
        ? '<div class="message-text mb-3"><i class="bi bi-info-circle"></i> Ara s\'està enregistrant: els canvis s\'aplicaran al pròxim enregistrament.</div>'
        : ''}

      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary js-cancel" type="button">Cancel·lar</button>
        <button class="btn btn-primary" value="save"><i class="bi bi-check-lg"></i> Desar</button>
      </div>
    </form>`;

  const nameInput = /** @type {HTMLInputElement} */ (dialog.querySelector('#rs-name'));
  const folderInput = /** @type {HTMLInputElement} */ (dialog.querySelector('.js-folder'));
  const example = /** @type {HTMLElement} */ (dialog.querySelector('.js-example'));

  // Exemple del nom de fitxer que sortirà (com el que crea el motor).
  const showExample = () => {
    const name = nameInput.value.trim() || `Enregistrador ${recorder.id}`;
    example.textContent = `${name}_AAAA-MM-DD_HH-MM-SS.mp3`;
  };
  nameInput.addEventListener('input', showExample);
  showExample();

  /** @type {HTMLElement} */ (dialog.querySelector('.js-browse')).addEventListener('click', async () => {
    const folder = await bridge?.chooseFolder(folderInput.value);
    if (folder) {
      folderInput.value = folder;
      folderInput.title = folder;
    }
  });
  /** @type {HTMLElement} */ (dialog.querySelector('.js-cancel')).addEventListener('click', () => dialog.close('cancel'));

  document.body.appendChild(dialog);
  dialog.showModal();
  nameInput.select();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      // Intro dins del nom també desa (és el primer botó d'enviar del formulari). Esc cancel·la.
      resolve(dialog.returnValue === 'save' ? { name: nameInput.value.trim(), folder: folderInput.value } : null);
      dialog.remove();
    });
  });
}
