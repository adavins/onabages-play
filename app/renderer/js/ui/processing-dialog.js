// @ts-check
// Finestra del compressor/limitador d'un reproductor (fase 4, pas 4c; D-068).
// Els mateixos paràmetres que el plugin SqrSoft del Winamp. Cada canvi s'aplica de seguida (també
// mentre sona) i es desa al perfil: no hi ha «Desar» ni «Cancel·lar», només «Tancar».

import { escapeHtml } from './format.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/**
 * Paràmetres: nom al protocol, etiqueta, marge, pas i unitat. `log`: el control lliscant va en escala
 * logarítmica (temps), perquè sigui igual de fi a 4 ms que a 1450 ms.
 */
const FIELDS = [
  { key: 'inputGainDb', label: 'Guany d\'entrada', min: -20, max: 30, step: 0.1, unit: 'dB', hint: 'Gain' },
  { key: 'thresholdDb', label: 'Llindar', min: -40, max: 0, step: 0.1, unit: 'dB', hint: 'Threshold' },
  { key: 'ratio', label: 'Ràtio', min: 1, max: 100, step: 0.1, unit: ':1', hint: 'Ratio (100 = limitador)', log: true },
  { key: 'attackMs', label: 'Atac', min: 0.1, max: 200, step: 0.1, unit: 'ms', hint: 'AttackTime', log: true },
  { key: 'releaseMs', label: 'Alliberament', min: 10, max: 5000, step: 1, unit: 'ms', hint: 'ReleaseTime', log: true },
  { key: 'outputGainDb', label: 'Guany de sortida', min: -20, max: 20, step: 0.1, unit: 'dB', hint: 'OutputGain' },
  { key: 'ceilingDb', label: 'Sostre', min: -6, max: 0, step: 0.1, unit: 'dB', hint: 'la sortida no el passa mai' },
];

const SLIDER_STEPS = 1000;

/** @param {any} field @param {number} value */
function toSlider(field, value) {
  if (!field.log) return value;
  return Math.round(SLIDER_STEPS * Math.log(value / field.min) / Math.log(field.max / field.min));
}

/** @param {any} field @param {number} position */
function fromSlider(field, position) {
  if (!field.log) return position;
  const value = field.min * Math.pow(field.max / field.min, position / SLIDER_STEPS);
  const decimals = field.step < 1 ? 1 : 0;
  return Number(value.toFixed(decimals));
}

/**
 * @param {Store} store
 * @param {Request} request
 * @param {string} playerId
 */
export function openProcessingDialog(store, request, playerId) {
  const presets = /** @type {any[]} */ (store.settings?.processingPresets ?? []);
  /** @type {any} */
  let current = { ...(store.players.get(playerId)?.processing ?? {}) };

  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog proc-dialog';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-sliders"></i> Compressor/limitador · Reproductor ${escapeHtml(playerId)}</h2>

      <div class="proc-top">
        <div class="form-check form-switch">
          <input class="form-check-input js-enabled" type="checkbox" role="switch" id="proc-enabled">
          <label class="form-check-label" for="proc-enabled">Activat</label>
        </div>
        <select class="form-select form-select-sm js-preset" aria-label="Preset">
          <option value="">— Personalitzat —</option>
          ${presets.map((p) => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`).join('')}
        </select>
      </div>

      <div class="proc-fields">
        ${FIELDS.map((f) => `
          <label class="proc-label" for="proc-${f.key}" title="${escapeHtml(f.hint)}">${escapeHtml(f.label)}</label>
          <input class="form-range js-range" type="range" id="proc-${f.key}" data-key="${f.key}"
                 min="${f.log ? 0 : f.min}" max="${f.log ? SLIDER_STEPS : f.max}" step="${f.log ? 1 : f.step}">
          <input class="form-control form-control-sm proc-number js-number" type="number" data-key="${f.key}"
                 min="${f.min}" max="${f.max}" step="${f.step}" aria-label="${escapeHtml(f.label)}">
          <span class="proc-unit">${f.unit}</span>`).join('')}
        <span class="proc-label">Canals</span>
        <div class="form-check proc-link">
          <input class="form-check-input js-link" type="checkbox" id="proc-link">
          <label class="form-check-label" for="proc-link">Junts (StereoTie): els dos canals s'abaixen igual</label>
        </div>
      </div>

      <div class="proc-reduction">
        <span class="proc-label">Reducció</span>
        <div class="gr-bar"><div class="gr-fill js-gr-fill"></div></div>
        <span class="gr-value js-gr-value">0,0 dB</span>
      </div>
      <p class="proc-help">Ordre: pista → compressor → sostre → fader. Els canvis s'apliquen de seguida i es desen al perfil.</p>

      <div class="obp-dialog-buttons">
        <button class="btn btn-primary" value="close">Tancar</button>
      </div>
    </form>`;
  document.body.appendChild(dialog);

  const $ = (/** @type {string} */ selector) => /** @type {HTMLInputElement} */ (dialog.querySelector(selector));
  const enabled = $('.js-enabled');
  const preset = /** @type {HTMLSelectElement} */ (dialog.querySelector('.js-preset'));
  const link = $('.js-link');

  /** Mostra els valors (p. ex. en obrir-lo o després d'aplicar un preset). */
  function show() {
    enabled.checked = !!current.enabled;
    link.checked = !!current.stereoLink;
    for (const f of FIELDS) {
      $(`.js-range[data-key="${f.key}"]`).value = String(toSlider(f, current[f.key]));
      $(`.js-number[data-key="${f.key}"]`).value = String(current[f.key]);
    }
    // El preset que coincideix amb tots els valors, si n'hi ha cap.
    const match = presets.find((p) => Object.keys(p.processing).every((k) => k === 'enabled' || p.processing[k] === current[k]));
    preset.value = match?.id ?? '';
    dialog.classList.toggle('proc-off', !current.enabled);
  }

  // Els canvis s'envien al motor agrupats (com a molt cada 100 ms mentre s'arrossega un control).
  /** @type {Record<string, any>} */
  let pendingChanges = {};
  /** @type {number | undefined} */
  let timer;
  /** @param {Record<string, any>} changes */
  function send(changes) {
    Object.assign(current, changes);
    Object.assign(pendingChanges, changes);
    clearTimeout(timer);
    timer = window.setTimeout(flush, 100);
    show();
  }
  async function flush() {
    const changes = pendingChanges;
    pendingChanges = {};
    if (Object.keys(changes).length === 0) return;
    try {
      const result = await request('player.setProcessing', { player: playerId, processing: changes });
      if (Object.keys(pendingChanges).length === 0) {
        current = { ...result.player.processing };
        show();
      }
    } catch (err) {
      $('.proc-help').textContent = /** @type {Error} */ (err).message;
    }
  }

  enabled.addEventListener('change', () => send({ enabled: enabled.checked }));
  link.addEventListener('change', () => send({ stereoLink: link.checked }));
  dialog.querySelector('.proc-fields')?.addEventListener('input', (e) => {
    const input = /** @type {HTMLInputElement} */ (e.target);
    const field = FIELDS.find((f) => f.key === input.dataset.key);
    if (!field) return;
    const raw = input.classList.contains('js-range') ? fromSlider(field, Number(input.value)) : Number(input.value);
    if (!Number.isFinite(raw)) return;
    send({ [field.key]: Math.min(field.max, Math.max(field.min, raw)) });
  });
  preset.addEventListener('change', async () => {
    if (!preset.value) return;
    try {
      clearTimeout(timer);
      pendingChanges = {};
      const result = await request('player.setProcessing', { player: playerId, preset: preset.value });
      current = { ...result.player.processing };
      show();
    } catch (err) {
      $('.proc-help').textContent = /** @type {Error} */ (err).message;
    }
  });
  // Retorn en un número no ha de tancar la finestra.
  dialog.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && /** @type {HTMLElement} */ (e.target).tagName === 'INPUT') e.preventDefault();
  });

  // Reducció en directe (esdeveniment meters), amb una caiguda suau perquè es pugui llegir.
  const fill = /** @type {HTMLElement} */ (dialog.querySelector('.js-gr-fill'));
  const value = /** @type {HTMLElement} */ (dialog.querySelector('.js-gr-value'));
  let shown = 0;
  const onMeters = () => {
    const reduction = Number(store.meters.playerReduction?.[playerId] ?? 0);
    shown = Math.max(reduction, shown - 0.4);
    fill.style.width = `${Math.min(100, (shown / 20) * 100)}%`;
    value.textContent = `${shown.toFixed(1).replace('.', ',')} dB`;
  };
  store.addEventListener('meters', onMeters);

  show();
  dialog.showModal();
  dialog.addEventListener('close', () => {
    store.removeEventListener('meters', onMeters);
    flush();
    dialog.remove();
  });
}
