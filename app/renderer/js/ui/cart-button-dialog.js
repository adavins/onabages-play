// @ts-check
// Diàleg per editar un botó de la botonera (fase 5, pas 28d; D-130): nom, color, volum i fitxer.
// Retorna els canvis, o null si es cancel·la. Qui el crida envia les ordres (cart.assign / cart.configure).

import { escapeHtml, fileName } from './format.js';

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

/** Colors dels botons (el primer, null, és el de per defecte). Tots amb prou contrast amb la lletra blanca. */
export const CART_COLORS = [null, '#0d6efd', '#6f42c1', '#d63384', '#dc3545', '#d9600b', '#a07800', '#198754', '#0f8a7a'];

const COLOR_NAMES = ['Per defecte', 'Blau', 'Lila', 'Rosa', 'Vermell', 'Taronja', 'Mostassa', 'Verd', 'Verd blavós'];

/**
 * @param {any} button  el botó tal com el dona el motor (cart.buttons)
 * @param {{ minGainDb: number, maxGainDb: number, maxName: number }} limits
 * @returns {Promise<{ name: string, color: string, gainDb: number, path: string } | null>}
 *   color "" = el de per defecte; path només canvia si s'ha triat un altre fitxer
 */
export function editCartButton(button, limits) {
  let path = button.path;
  const current = button.color ?? null;
  // Un color que no és a la paleta (p. ex. posat des del protocol) també surt, perquè no es perdi en desar.
  const colors = CART_COLORS.includes(current) ? CART_COLORS : [...CART_COLORS, current];
  const names = CART_COLORS.includes(current) ? COLOR_NAMES : [...COLOR_NAMES, "El d'ara"];
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog cart-dialog';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-grid-3x3-gap-fill"></i> Botó ${button.index + 1} de la pàgina ${button.page + 1}</h2>
      <label class="form-label" for="cart-name">Nom</label>
      <input id="cart-name" class="form-control form-control-sm" type="text" maxlength="${limits.maxName}" value="${escapeHtml(button.name)}">

      <div class="form-label mt-3">Color</div>
      <div class="cart-colors" role="radiogroup" aria-label="Color del botó">
        ${colors.map((c, i) => `
          <label class="cart-color" title="${names[i]}" data-color="${c ?? '#3a4a5c'}">
            <input type="radio" name="cart-color" value="${c ?? ''}"${c === current ? ' checked' : ''}>
            <span><i class="bi bi-check-lg"></i></span>
          </label>`).join('')}
      </div>

      <label class="form-label mt-3" for="cart-gain">Volum: <strong class="js-gain-value"></strong></label>
      <input id="cart-gain" class="form-range" type="range" min="${limits.minGainDb}" max="${limits.maxGainDb}" step="0.5" value="${button.gainDb ?? 0}">

      <div class="form-label mt-3">Fitxer</div>
      <div class="cart-file">
        <span class="js-file" title="${escapeHtml(path)}">${escapeHtml(fileName(path))}</span>
        <button class="btn btn-sm btn-outline-secondary js-change" type="button"><i class="bi bi-folder2-open"></i> Canviar…</button>
      </div>

      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary js-cancel" type="button">Cancel·lar</button>
        <button class="btn btn-primary" value="save">Desar</button>
      </div>
    </form>`;
  document.body.appendChild(dialog);
  // El color de cada mostra es posa des d'aquí: la política de seguretat de la pàgina no deixa posar estils dins de l'HTML.
  for (const swatch of dialog.querySelectorAll('.cart-color')) {
    /** @type {HTMLElement} */ (swatch).style.setProperty('--cart-color', /** @type {HTMLElement} */ (swatch).dataset.color ?? '');
  }

  const $ = (/** @type {string} */ sel) => /** @type {HTMLInputElement} */ (dialog.querySelector(sel));
  const gain = $('#cart-gain');
  const showGain = () => {
    const v = Number(gain.value);
    $('.js-gain-value').textContent = `${v > 0 ? '+' : v < 0 ? '−' : ''}${Math.abs(v).toFixed(1).replace('.', ',')} dB`;
  };
  // Retorn desa (el primer botó de debò és Desar); Cancel·lar i Esc no.
  $('.js-cancel').addEventListener('click', () => dialog.close('cancel'));
  gain.addEventListener('input', showGain);
  gain.addEventListener('dblclick', () => { gain.value = '0'; showGain(); }); // doble clic: torna a 0 dB
  showGain();
  $('.js-change').addEventListener('click', async () => {
    const [chosen] = (await bridge?.openAudioFiles({ title: 'Triar el fitxer del botó', single: true })) ?? [];
    if (!chosen) return;
    path = chosen;
    $('.js-file').textContent = fileName(chosen);
    $('.js-file').title = chosen;
  });

  dialog.showModal();
  $('#cart-name').select();
  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      const color = /** @type {HTMLInputElement | null} */ (dialog.querySelector('input[name="cart-color"]:checked'))?.value ?? '';
      resolve(dialog.returnValue === 'save'
        ? { name: $('#cart-name').value.trim(), color, gainDb: Number(gain.value), path }
        : null);
      dialog.remove();
    });
  });
}
