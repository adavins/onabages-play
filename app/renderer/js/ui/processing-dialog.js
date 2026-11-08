// @ts-check
// Finestra del compressor/limitador d'un reproductor (fase 4, pas 4c; D-068) o de l'emissor d'streaming (fase 5, pas 31d).
// Els mateixos paràmetres que el plugin SqrSoft del Winamp. Cada canvi s'aplica de seguida (també
// mentre sona) i es desa al perfil: no hi ha «Desar» ni «Cancel·lar», només «Tancar».

import { escapeHtml } from './format.js';
import { askText, choose } from './dialogs.js';

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
 * A qui s'aplica la finestra: un reproductor o l'emissor d'streaming (fase 5, pas 31d).
 * @typedef {object} ProcessingTarget
 * @property {string} title  el que surt al títol («Reproductor 1», «Streaming»)
 * @property {string | null} playerId  el reproductor (amb les seccions de fos i silencis), o null
 * @property {() => any} processing  els ajustos actuals
 * @property {(params: object) => Promise<any>} set  envia { processing } o { preset } i retorna els ajustos nous
 * @property {() => number} reduction  quant abaixa ara (dB), de l'esdeveniment meters
 * @property {string} help  l'ordre del processament
 */

/**
 * @param {Store} store
 * @param {Request} request
 * @param {string} playerId
 */
export function openProcessingDialog(store, request, playerId) {
  openDialog(store, request, {
    title: `Reproductor ${playerId}`,
    playerId,
    processing: () => store.players.get(playerId)?.processing,
    set: async (params) => (await request('player.setProcessing', { player: playerId, ...params })).player.processing,
    reduction: () => Number(store.meters.playerReduction?.[playerId] ?? 0),
    help: 'Ordre: pista → compressor → sostre → fader. Els canvis s\'apliquen de seguida i es desen al perfil.',
  });
}

/**
 * El compressor (DSP) de l'emissor d'streaming (fase 5, pas 31d; D-140): el mateix que el dels reproductors.
 * @param {Store} store
 * @param {Request} request
 */
export function openStreamProcessingDialog(store, request) {
  openDialog(store, request, {
    title: 'Streaming',
    playerId: null,
    processing: () => store.stream?.processing,
    set: async (params) => (await request('stream.setProcessing', params)).stream.processing,
    reduction: () => Number(store.meters.streamReduction?.processing ?? 0),
    help: 'Ordre: entrada → fader → NIV → compressor → LIM → MP3. Els canvis s\'apliquen de seguida, també mentre s\'emet, i es desen al perfil.',
  });
}

/**
 * @param {Store} store
 * @param {Request} request
 * @param {ProcessingTarget} target
 */
function openDialog(store, request, target) {
  const playerId = target.playerId;
  /** Presets de l'aplicació (builtIn) i propis del perfil. Canvien en desar-ne o esborrar-ne un. */
  let presets = /** @type {any[]} */ (store.settings?.processingPresets ?? []);
  /** @type {any} */
  let current = { ...(target.processing() ?? {}) };

  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog proc-dialog';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-sliders"></i> Processament · ${escapeHtml(target.title)}</h2>
      ${playerId === null ? '' : `

      <!-- Encadenament amb fos (fase 5; D-077). -->
      <h3 class="obp-settings-section">Encadenament entre pistes</h3>
      <div class="proc-crossfade">
        <label class="proc-label" for="proc-crossfade">Fos</label>
        <input class="form-control form-control-sm proc-number js-crossfade" type="number" id="proc-crossfade"
               min="0" max="10000" step="10" aria-label="Fos entre pistes, en mil·lisegons">
        <span class="proc-unit">ms</span>
        <div class="btn-group btn-group-sm js-crossfade-quick" role="group" aria-label="Valors habituals">
          ${[0, 10, 100, 500, 1000, 3000].map((ms) => `<button type="button" class="btn btn-outline-secondary" data-ms="${ms}">${ms === 0 ? 'Cap' : ms < 1000 ? `${ms} ms` : `${ms / 1000} s`}</button>`).join('')}
        </div>
      </div>
      <p class="proc-help">Quan una pista s'encadena sola amb la següent (avanç automàtic) o se'n fa sonar una altra a mà
        mentre sona, se sobreposen aquest temps: l'una baixa i l'altra puja. 0 = sense fos. No s'aplica als bucles.
        En l'avanç automàtic, s'aplica a partir de la pista següent.</p>

      <!-- Retall de silencis (fase 5; D-081). El llindar i els marges són de ⚙ Configuració (comuns al perfil). -->
      <h3 class="obp-settings-section">Silencis</h3>
      <div class="form-check form-switch">
        <input class="form-check-input js-trim" type="checkbox" role="switch" id="proc-trim">
        <label class="form-check-label" for="proc-trim">Retallar el silenci del principi i del final de cada pista</label>
      </div>
      <p class="proc-help js-trim-help"></p>`}

      <h3 class="obp-settings-section">Compressor/limitador</h3>
      <div class="proc-top">
        <div class="form-check form-switch">
          <input class="form-check-input js-enabled" type="checkbox" role="switch" id="proc-enabled">
          <label class="form-check-label" for="proc-enabled">Activat</label>
        </div>
        <div class="proc-presets">
          <select class="form-select form-select-sm js-preset" aria-label="Preset"></select>
          <button type="button" class="btn btn-sm btn-outline-secondary js-save-preset" title="Desar aquests ajustos com a preset propi…">
            <i class="bi bi-bookmark-plus"></i> Desar…
          </button>
          <button type="button" class="btn btn-sm btn-outline-danger js-delete-preset" title="Esborrar aquest preset propi">
            <i class="bi bi-trash"></i>
          </button>
        </div>
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
      <p class="proc-help js-help"></p>

      <div class="obp-dialog-buttons">
        <button class="btn btn-primary" value="close">Tancar</button>
      </div>
    </form>`;
  document.body.appendChild(dialog);

  const $ = (/** @type {string} */ selector) => /** @type {HTMLInputElement} */ (dialog.querySelector(selector));
  const enabled = $('.js-enabled');
  const preset = /** @type {HTMLSelectElement} */ (dialog.querySelector('.js-preset'));
  const link = $('.js-link');

  // Només als reproductors: retall de silencis i fos entre pistes.
  if (playerId !== null) {
    // --- Retall de silencis ---
    const trimSwitch = $('.js-trim');
    const trimHelp = /** @type {HTMLElement} */ (dialog.querySelector('.js-trim-help'));
    const trimSettings = store.settings?.trim ?? { thresholdDb: -60, leadMs: 10, tailMs: 50 };
    trimSwitch.checked = !!store.players.get(playerId)?.trim?.enabled;
    trimHelp.textContent = `Cada pista comença ${trimSettings.leadMs} ms abans del primer so i s'acaba ${trimSettings.tailMs} ms després `
      + `de l'últim (silenci: per sota de ${String(trimSettings.thresholdDb).replace('-', '−')} dBFS). El fitxer no es toca. `
      + 'El llindar i els marges es canvien a ⚙ Configuració. S\'aplica a partir de la pista següent.';
    trimSwitch.addEventListener('change', async () => {
      try {
        const result = await request('player.setTrim', { player: playerId, enabled: trimSwitch.checked });
        trimSwitch.checked = !!result.player.trim.enabled;
      } catch (err) {
        trimSwitch.checked = !trimSwitch.checked;
        $('.js-help').textContent = /** @type {Error} */ (err).message;
      }
    });
  
    // --- Encadenament amb fos ---
    const crossfade = $('.js-crossfade');
    const quick = /** @type {HTMLElement} */ (dialog.querySelector('.js-crossfade-quick'));
    /** Marca el botó del valor habitual que coincideix (si n'hi ha). @param {number} ms */
    const showCrossfade = (ms) => {
      crossfade.value = String(ms);
      for (const b of quick.querySelectorAll('button')) {
        b.classList.toggle('active', Number(/** @type {HTMLElement} */ (b).dataset.ms) === ms);
      }
    };
    showCrossfade(Number(store.players.get(playerId)?.crossfadeMs ?? 0));
    /** @type {number | undefined} */
    let crossfadeTimer;
    const sendCrossfade = (/** @type {number} */ ms) => {
      showCrossfade(ms);
      clearTimeout(crossfadeTimer);
      crossfadeTimer = window.setTimeout(async () => {
        try {
          const result = await request('player.setCrossfade', { player: playerId, ms });
          showCrossfade(result.player.crossfadeMs);
        } catch (err) {
          $('.js-help').textContent = /** @type {Error} */ (err).message;
        }
      }, 300);
    };
    crossfade.addEventListener('input', () => {
      const ms = Math.round(Number(crossfade.value));
      if (Number.isFinite(ms) && ms >= 0 && ms <= 10000) sendCrossfade(ms);
    });
    quick.addEventListener('click', (e) => {
      const button = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('[data-ms]'));
      if (button) sendCrossfade(Number(button.dataset.ms));
    });
  }

  $('.js-help').textContent = target.help;

  const saveButton = /** @type {HTMLButtonElement} */ (dialog.querySelector('.js-save-preset'));
  const deleteButton = /** @type {HTMLButtonElement} */ (dialog.querySelector('.js-delete-preset'));

  /** Opcions del desplegable: els de l'aplicació i, a part, els propis. */
  function renderPresets() {
    const option = (/** @type {any} */ p) => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`;
    const builtIn = presets.filter((p) => p.builtIn);
    const own = presets.filter((p) => !p.builtIn);
    preset.innerHTML = '<option value="">— Personalitzat —</option>'
      + `<optgroup label="De l'aplicació">${builtIn.map(option).join('')}</optgroup>`
      + (own.length ? `<optgroup label="Els meus presets">${own.map(option).join('')}</optgroup>` : '');
  }

  /** Els presets han canviat (desat o esborrat): també per a les altres finestres. @param {any[]} list */
  function setPresets(list) {
    presets = list;
    if (store.settings) store.settings.processingPresets = list;
    renderPresets();
  }

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
    deleteButton.disabled = !match || match.builtIn;
    dialog.classList.toggle('proc-off', !current.enabled);
  }

  // Desar els ajustos actuals com a preset propi (si el nom ja hi és, es demana si se substitueix).
  saveButton.addEventListener('click', async () => {
    const selected = presets.find((p) => p.id === preset.value && !p.builtIn);
    const name = await askText({
      title: 'Desar com a preset',
      icon: 'bi-bookmark-plus',
      label: 'Nom del preset (p. ex. «Música forta» o «Veu»)',
      value: selected?.name ?? '',
      okLabel: 'Desar',
      maxLength: 40,
    });
    if (name === null) return;
    const existing = presets.find((p) => !p.builtIn && p.name.toLowerCase() === name.toLowerCase());
    if (existing) {
      const choice = await choose({
        title: 'Substituir el preset',
        icon: 'bi-exclamation-triangle-fill',
        message: `Ja tens un preset que es diu «${existing.name}». Vols substituir-lo pels ajustos actuals?`,
        buttons: [
          { label: 'Cancel·lar', value: 'cancel' },
          { label: 'Substituir', value: 'replace', style: 'primary' },
        ],
      });
      if (choice !== 'replace') return;
    }
    try {
      await flush();
      const result = await request('processing.savePreset', { name, processing: current });
      setPresets(result.processingPresets);
      show();
    } catch (err) {
      $('.js-help').textContent = /** @type {Error} */ (err).message;
    }
  });

  deleteButton.addEventListener('click', async () => {
    const selected = presets.find((p) => p.id === preset.value && !p.builtIn);
    if (!selected) return;
    const choice = await choose({
      title: 'Esborrar el preset',
      icon: 'bi-trash',
      message: `Vols esborrar el preset «${selected.name}»? Els reproductors que el fan servir no canvien.`,
      buttons: [
        { label: 'Cancel·lar', value: 'cancel' },
        { label: 'Esborrar', value: 'delete', style: 'danger' },
      ],
    });
    if (choice !== 'delete') return;
    try {
      const result = await request('processing.deletePreset', { preset: selected.id });
      setPresets(result.processingPresets);
      show();
    } catch (err) {
      $('.js-help').textContent = /** @type {Error} */ (err).message;
    }
  });

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
      const processing = await target.set({ processing: changes });
      if (Object.keys(pendingChanges).length === 0) {
        current = { ...processing };
        show();
      }
    } catch (err) {
      $('.js-help').textContent = /** @type {Error} */ (err).message;
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
      current = { ...(await target.set({ preset: preset.value })) };
      show();
    } catch (err) {
      $('.js-help').textContent = /** @type {Error} */ (err).message;
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
    const reduction = target.reduction();
    shown = Math.max(reduction, shown - 0.4);
    fill.style.width = `${Math.min(100, (shown / 20) * 100)}%`;
    value.textContent = `${shown.toFixed(1).replace('.', ',')} dB`;
  };
  store.addEventListener('meters', onMeters);

  renderPresets();
  show();
  dialog.showModal();
  dialog.addEventListener('close', () => {
    store.removeEventListener('meters', onMeters);
    flush();
    dialog.remove();
  });
}
