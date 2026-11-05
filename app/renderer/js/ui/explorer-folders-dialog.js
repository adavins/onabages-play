// @ts-check
// Diàleg per configurar les carpetes d'una pestanya de l'explorador (fase 4, pas 2c; D-064).
// Cada carpeta té un nom (el que es veu al desplegable) i una ruta. Es poden afegir (diàleg de
// Windows), canviar de nom, reordenar i treure. Es desen al perfil actiu (explorer.setFolders).

import { escapeHtml } from './format.js';

/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

const MAX_FOLDERS = 20;
const MAX_NAME = 40;

/**
 * @param {'audios' | 'lists'} kind
 * @param {string} title  nom de la pestanya
 * @param {{ name: string, path: string }[]} current
 * @param {Request} request
 * @returns {Promise<any | null>} el resultat d'explorer.setFolders (les carpetes de les dues pestanyes), o null si es cancel·la
 */
export function editExplorerFolders(kind, title, current, request) {
  /** @type {{ name: string, path: string }[]} */
  const folders = current.map((f) => ({ name: f.name, path: f.path }));

  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog ex-folders-dialog';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-gear"></i> Carpetes de «${escapeHtml(title)}»</h2>
      <p class="ex-folders-help">Són les carpetes d'aquest perfil. El nom és el que surt al desplegable.</p>
      <div class="ex-folders-list js-list"></div>
      <button type="button" class="btn btn-sm btn-outline-primary js-add"><i class="bi bi-plus-lg"></i> Afegir una carpeta…</button>
      <div class="ex-folders-error js-error" role="alert"></div>
      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary" value="cancel">Cancel·lar</button>
        <button class="btn btn-primary js-save" value="save">Desar</button>
      </div>
    </form>`;
  document.body.appendChild(dialog);

  const list = /** @type {HTMLElement} */ (dialog.querySelector('.js-list'));
  const error = /** @type {HTMLElement} */ (dialog.querySelector('.js-error'));
  const add = /** @type {HTMLButtonElement} */ (dialog.querySelector('.js-add'));

  function render() {
    list.innerHTML = folders.length === 0
      ? '<div class="ex-folders-empty">Cap carpeta.</div>'
      : folders.map((f, i) => `
        <div class="ex-folders-row" data-index="${i}">
          <input class="form-control form-control-sm js-name" type="text" maxlength="${MAX_NAME}"
                 value="${escapeHtml(f.name)}" aria-label="Nom de la carpeta">
          <span class="ex-folders-path" title="${escapeHtml(f.path)}">${escapeHtml(f.path)}</span>
          <button type="button" class="btn btn-sm btn-outline-secondary" data-move="-1" title="Amunt" ${i === 0 ? 'disabled' : ''}><i class="bi bi-arrow-up"></i></button>
          <button type="button" class="btn btn-sm btn-outline-secondary" data-move="1" title="Avall" ${i === folders.length - 1 ? 'disabled' : ''}><i class="bi bi-arrow-down"></i></button>
          <button type="button" class="btn btn-sm btn-outline-danger" data-remove title="Treure de la llista (la carpeta no s'esborra)"><i class="bi bi-x-lg"></i></button>
        </div>`).join('');
    add.disabled = folders.length >= MAX_FOLDERS;
  }

  list.addEventListener('input', (e) => {
    const input = /** @type {HTMLInputElement} */ (e.target);
    const row = /** @type {HTMLElement | null} */ (input.closest('[data-index]'));
    if (row) folders[Number(row.dataset.index)].name = input.value;
  });

  // Retorn en un nom no ha de tancar el diàleg (el formulari faria servir el primer botó, Cancel·lar).
  list.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') e.preventDefault();
  });

  list.addEventListener('click', (e) => {
    const button = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('button'));
    const row = /** @type {HTMLElement | null} */ (button?.closest('[data-index]') ?? null);
    if (!button || !row) return;
    const i = Number(row.dataset.index);
    if (button.dataset.move) {
      const j = i + Number(button.dataset.move);
      [folders[i], folders[j]] = [folders[j], folders[i]];
    } else if (button.hasAttribute('data-remove')) {
      folders.splice(i, 1);
    }
    render();
  });

  add.addEventListener('click', async () => {
    const path = await bridge?.chooseFolder(folders.at(-1)?.path ?? '');
    if (!path) return;
    if (folders.some((f) => f.path.toLowerCase() === path.toLowerCase())) {
      error.textContent = 'Aquesta carpeta ja hi és.';
      return;
    }
    error.textContent = '';
    const name = path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path;
    folders.push({ name: name.slice(0, MAX_NAME), path });
    render();
  });

  render();
  dialog.showModal();

  return new Promise((resolve) => {
    // «Desar»: es comprova al motor abans de tancar (si hi ha un error, el diàleg es queda obert).
    dialog.querySelector('.js-save')?.addEventListener('click', async (e) => {
      e.preventDefault();
      try {
        const result = await request('explorer.setFolders', {
          kind,
          folders: folders.map((f) => ({ name: f.name.trim(), path: f.path })),
        });
        dialog.remove();
        resolve(result);
      } catch (err) {
        error.textContent = /** @type {Error} */ (err).message;
      }
    });
    dialog.addEventListener('close', () => {
      if (dialog.isConnected) {
        dialog.remove();
        resolve(null);
      }
    });
  });
}
