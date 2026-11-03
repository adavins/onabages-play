// @ts-check
// Menú contextual (botó dret). Es tanca en clicar fora, en prémer Esc o en triar una opció.
//
// Ús:
//   showContextMenu(event, [
//     { label: 'Informació', icon: 'bi-info-circle', action: () => ... },
//   ]);

import { escapeHtml } from './format.js';

/** @typedef {{ label: string, icon?: string, action: () => void }} MenuItem */

/** @type {HTMLElement | null} */
let open = null;

/**
 * @param {MouseEvent} event  l'esdeveniment contextmenu (per saber on es mostra)
 * @param {MenuItem[]} items
 */
export function showContextMenu(event, items) {
  event.preventDefault();
  closeContextMenu();

  const menu = document.createElement('div');
  menu.className = 'obp-menu dropdown-menu show';
  menu.innerHTML = items.map((item, i) => `
    <button class="dropdown-item" type="button" data-index="${i}">
      ${item.icon ? `<i class="bi ${item.icon}"></i> ` : ''}${escapeHtml(item.label)}
    </button>`).join('');

  menu.addEventListener('click', (e) => {
    const button = /** @type {HTMLElement} */ (e.target).closest('[data-index]');
    if (!button) return;
    closeContextMenu();
    items[Number(/** @type {HTMLElement} */ (button).dataset.index)].action();
  });

  document.body.appendChild(menu);

  // Dins la finestra, encara que es cliqui a prop de la vora.
  const { innerWidth, innerHeight } = window;
  const rect = menu.getBoundingClientRect();
  menu.style.left = `${Math.min(event.clientX, innerWidth - rect.width - 4)}px`;
  menu.style.top = `${Math.min(event.clientY, innerHeight - rect.height - 4)}px`;
  open = menu;
}

export function closeContextMenu() {
  open?.remove();
  open = null;
}

window.addEventListener('mousedown', (e) => {
  if (open && !open.contains(/** @type {Node} */ (e.target))) closeContextMenu();
});
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') closeContextMenu();
});
window.addEventListener('blur', closeContextMenu);
