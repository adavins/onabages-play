// @ts-check
// Botó «?» de la capçalera (fase 5, D-092): menú amb el manual d'usuari i la documentació tècnica.
// Els documents s'obren al navegador predeterminat de Windows (no dins de l'aplicació).

import { showContextMenu } from './context-menu.js';
import { choose } from './dialogs.js';

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

/** @param {HTMLButtonElement} button */
export function setupHelpMenu(button) {
  button.addEventListener('click', () => {
    // Sota el botó (també si s'ha obert amb el teclat), com el menú de perfils.
    const rect = button.getBoundingClientRect();
    const where = new MouseEvent('click', { clientX: rect.left, clientY: rect.bottom + 2 });
    showContextMenu(where, [
      { label: 'Manual d\'usuari', icon: 'bi-book', action: () => open('manual') },
      { label: 'Documentació tècnica', icon: 'bi-tools', action: () => open('technical') },
    ]);
  });
}

async function open(/** @type {string} */ name) {
  const error = await bridge?.openHelp(name);
  if (!error) return;
  await choose({
    title: 'Ajuda',
    icon: 'bi-exclamation-triangle',
    message: `No s'ha pogut obrir el document.\n${error}`,
    buttons: [{ label: 'D\'acord', value: 'ok', style: 'primary' }],
  });
}
