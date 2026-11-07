// @ts-check
// Panell d'un enregistrador (especificació 8.3): Enregistrar → Aturar → Desar o Descartar.
// La interfície només envia ordres i mostra l'estat que rep del motor.
//
// - Enregistrar: comença de seguida, sempre en un fitxer nou (també des de «Pendent»).
// - Aturar: cal prémer-lo dues vegades (la primera el «arma» uns segons), per evitar aturades accidentals.
// - Pendent: Escoltar (preescolta), Desar (diàleg de Windows) o Descartar (paperera, amb confirmació).
// - LIM (fase 5, D-083): limitador del que es desa al fitxer; es pot activar i desactivar en qualsevol moment.

import { RECORDER_STATES, stateBadge } from './states.js';
import { deviceWarning } from './device-line.js';
import { DeviceSelect } from './device-select.js';
import { LevelMeter } from './meter.js';
import { VolumeFader } from './fader.js';
import { editRecorderSettings } from './recorder-settings.js';
import { saveRecording, discardRecording } from './recording-actions.js';
import { togglePreview } from './cue-actions.js';
import { escapeHtml, fileName, formatBytes, formatDuration } from './format.js';
import { HANDLE_HTML } from './panel-order.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** Quant temps queda «armat» el botó d'Aturar després de la primera pulsació. */
const STOP_CONFIRM_MS = 3000;

/** Quant temps es mostra un missatge (error d'una ordre, avís en desar...). */
const MESSAGE_MS = 6000;

/** Escala de la barra del limitador (dB): un limitador abaixa poc, i així es veu bé. */
const LIMITER_SCALE_DB = 12;

/** Estats des dels quals es pot començar un enregistrament (docs/protocol.md, 4.3). */
const CAN_START = ['idle', 'pending', 'error'];

export class RecorderPanel {
  /** @type {HTMLElement} */ element;
  #store;
  #id;
  #request;
  #deviceSelect;
  #meter;
  #fader;
  /** @type {Record<string, HTMLElement>} */ #el = {};
  /** @type {number | undefined} */ #messageTimer;
  /** Primera pulsació d'Aturar feta: fins quan s'espera la segona. @type {number | undefined} */
  #stopArmedTimer;
  /** Fitxer de l'últim enregistrament començat (per reiniciar l'indicador de saturació). */
  /** @type {string | null} */ #lastFile = null;
  /** Reducció del limitador que es mostra (cau a poc a poc, perquè es pugui llegir). */
  #shownReduction = 0;

  /**
   * @param {Store} store
   * @param {string} id  identificador de l'enregistrador ("1", "2"...)
   * @param {Request} request  envia una ordre al motor
   */
  constructor(store, id, request) {
    this.#store = store;
    this.#id = id;
    this.#request = request;

    this.element = document.createElement('section');
    this.element.className = 'card panel recorder';
    this.element.setAttribute('aria-label', `Enregistrador ${id}`);
    this.element.dataset.panelId = id; // per canviar-lo de lloc (panel-order.js)
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-header-left">${HANDLE_HTML}<button class="panel-title rec-title js-title" type="button" title="Canviar el nom i la carpeta">
          <i class="bi bi-record-circle"></i> <span class="js-name"></span> <i class="bi bi-pencil rec-edit"></i>
        </button></span>
        <span class="panel-header-right">
          <button class="btn btn-sm dsp-btn js-lim" type="button"><i class="d-none"></i><span>LIM</span></button>
          <span class="js-state"></span>
        </span>
      </div>
      <div class="card-body panel-body">
        <div class="js-device-slot"></div>
        <div class="js-device-warning"></div>
        <div class="js-meter-slot"></div>
        <div class="input-off js-input-off">Entrada no disponible: el mesurador s'activarà quan torni.</div>
        <div class="js-fader-slot"></div>
        <div class="gr-row js-gr d-none" title="Quant abaixa ara el limitador (només mentre s'enregistra)">
          <span class="gr-caption">Limitador</span>
          <div class="gr-bar"><div class="gr-fill js-gr-fill"></div></div>
          <span class="gr-value js-gr-value">0,0 dB</span>
        </div>

        <div class="rec-info">
          <div class="elapsed js-elapsed">0:00</div>
          <div class="rec-file">
            <div class="track js-file"></div>
            <div class="rec-sizes js-sizes"></div>
          </div>
        </div>
        <div class="rec-low-disk js-low-disk"></div>
        <div class="rec-low-disk js-archive-error"></div>

        <div class="controls">
          <button class="btn btn-danger btn-control rec-start js-start" type="button" title="Enregistrar (sempre en un fitxer nou)">
            <i class="bi bi-record-fill"></i><span class="rec-label"> Enregistrar</span>
          </button>
          <button class="btn btn-outline-light btn-control rec-stop js-stop" type="button" title="Aturar (cal prémer-lo dues vegades)">
            <i class="bi bi-stop-fill"></i><span class="rec-label js-stop-label"> Aturar</span>
          </button>
          <button class="btn btn-outline-light btn-control rec-action rec-preview preview-btn js-preview d-none" type="button" title="Escoltar-lo per la preescolta">
            <i class="bi bi-headphones"></i><span class="rec-label js-preview-label"> Escoltar</span>
          </button>
          <button class="btn btn-outline-light btn-control rec-action js-edit d-none" type="button" title="Obrir-lo a l'editor">
            <i class="bi bi-scissors"></i><span class="rec-label"> Editar</span>
          </button>
          <button class="btn btn-primary btn-control rec-action js-save d-none" type="button" title="Desar: triar el nom i la carpeta">
            <i class="bi bi-floppy"></i><span class="rec-label"> Desar</span>
          </button>
          <button class="btn btn-outline-danger btn-control rec-action js-discard d-none" type="button" title="Descartar: enviar a la paperera">
            <i class="bi bi-trash3"></i><span class="rec-label"> Descartar</span>
          </button>
        </div>

        <div class="error-text js-error"></div>
        <div class="message-text js-message"></div>
      </div>`;

    for (const node of this.element.querySelectorAll('[class*="js-"]')) {
      const name = [...node.classList].find((c) => c.startsWith('js-'));
      if (name) this.#el[name.slice(3)] = /** @type {HTMLElement} */ (node);
    }

    this.#deviceSelect = new DeviceSelect('input', (deviceId) => this.#setDevice(deviceId));
    this.#el['device-slot'].appendChild(this.#deviceSelect.element);

    this.#meter = new LevelMeter();
    this.#el['meter-slot'].appendChild(this.#meter.element);
    this.#fader = new VolumeFader((db) => this.#send('recorder.setGain', { db }),
      { min: -60, max: 12, icon: 'bi-mic', label: 'Volum d\'enregistrament' });
    this.#el['fader-slot'].appendChild(this.#fader.element);

    this.#el.title.addEventListener('click', () => this.#editSettings());
    // LIM: activa o desactiva el limitador (de seguida, també a mig enregistrament).
    this.#el.lim.addEventListener('click', () => {
      const enabled = !!this.#store.recorders.get(this.#id)?.limiter?.enabled;
      this.#send('recorder.setLimiter', { enabled: !enabled });
    });
    this.#el.start.addEventListener('click', () => this.#send('recorder.start'));
    this.#el.stop.addEventListener('click', () => this.#stopPressed());
    this.#el.preview.addEventListener('click', () => this.#preview());
    this.#el.edit.addEventListener('click', () => {
      const item = this.#store.pendingRecording(this.#id);
      if (item) /** @type {any} */ (window).onabages?.openEditor(item.path); // finestra de l'editor (fase 3)
    });
    this.#el.save.addEventListener('click', () => this.#save());
    this.#el.discard.addEventListener('click', () => this.#discard());

    this.render();
  }

  render() {
    const r = this.#store.recorders.get(this.#id);
    if (!r) return;
    const el = this.#el;
    const recording = r.state === 'recording';
    const pending = r.state === 'pending';

    // Menys soroll visual (fase 5, pas 14c; D-100): inactiu = atenuat i mesurador en grisos (o amagat, si es
    // vol); enregistrant = vora i capçalera vermelles; pendent = vora groga. Ho fa el CSS amb aquestes classes.
    const idle = r.state === 'idle';
    this.element.classList.toggle('rec-idle', idle);
    this.element.classList.toggle('rec-recording', recording);
    this.element.classList.toggle('rec-pending', pending);
    this.element.classList.toggle('rec-hide-meter', idle && !!this.#store.settings?.ui?.hideIdleRecorderMeters);

    el.name.textContent = r.name ?? `Enregistrador ${this.#id}`;
    el.title.title = `Canviar el nom i la carpeta\nCarpeta: ${r.folder ?? ''}`;
    el.state.innerHTML = stateBadge(RECORDER_STATES[r.state]);
    this.#deviceSelect.update(this.#store, r.deviceId, recording);
    el['device-warning'].innerHTML = deviceWarning(this.#store, r.deviceId);

    // Amb dispositiu però sense entrada oberta, el mesurador no pot funcionar: es diu clarament.
    const inputOff = !!r.deviceId && !r.inputActive;
    this.#meter.setActive(!inputOff);
    el['input-off'].classList.toggle('d-none', !inputOff);
    this.#fader.update(r.gainDb ?? 0);

    // Limitador (D-083): botó encès (color i marca ✓) i barra de reducció.
    const limiter = r.limiter ?? {};
    const ceiling = formatDb(limiter.ceilingDb ?? -1);
    el.lim.classList.toggle('on', !!limiter.enabled);
    el.lim.setAttribute('aria-pressed', String(!!limiter.enabled));
    el.lim.title = limiter.enabled
      ? `Limitador ACTIVAT: el que es desa no passa de ${ceiling} dB. Clica per desactivar-lo (de seguida, també mentre s'enregistra). El sostre es canvia a ⚙ Configuració.`
      : `Limitador desactivat. Clica per activar-lo: el que es desi no passarà de ${ceiling} dB (de seguida, també mentre s'enregistra).`;
    /** @type {HTMLElement} */ (el.lim.querySelector('i')).className = limiter.enabled ? 'bi bi-check-lg' : 'd-none';
    el.gr.classList.toggle('d-none', !limiter.enabled);

    // Temps, fitxer, mida i espai lliure.
    el.elapsed.textContent = formatDuration(r.elapsed ?? 0);
    el.elapsed.classList.toggle('dim', !recording && !pending);
    el.elapsed.classList.toggle('recording', recording);
    el.file.textContent = r.file ? fileName(r.file) : 'Cap fitxer';
    el.file.classList.toggle('dim', !r.file);
    el.sizes.innerHTML = this.#sizesText(r);
    el['low-disk'].innerHTML = r.lowDiskSpace
      ? `<i class="bi bi-exclamation-triangle-fill"></i> Poc espai al disc: queden ${escapeHtml(formatBytes(r.freeDiskBytes))}`
      : '';

    // Còpia d'historial (D-086): si no s'ha pogut fer o s'ha interromput, es diu (l'enregistrament continua).
    el['archive-error'].innerHTML = r.archive?.error
      ? `<i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(r.archive.error)}`
      : '';
    el['archive-error'].title = r.archive?.errorDetail ?? '';
    el.file.title = r.archive?.path ? `${r.file ?? ''}\nCòpia d'historial: ${r.archive.path}` : (r.file ?? '');

    // Enregistrament nou: l'indicador de saturació torna a començar.
    if (recording && r.file !== this.#lastFile) {
      this.#lastFile = r.file;
      this.#meter.resetClip();
    }

    // Botons: cada un només quan el motor l'accepta.
    setEnabled(el.start, CAN_START.includes(r.state) && !!r.deviceId);
    el.stop.classList.toggle('d-none', pending);
    setEnabled(el.stop, recording);
    if (!recording) this.#disarmStop();
    const item = pending ? this.#store.pendingRecording(this.#id) : undefined;
    const previewing = !!item && this.#store.isPreviewing(item.path);
    el.preview.classList.toggle('d-none', !pending);
    el.preview.classList.toggle('on', previewing);
    el.preview.title = previewing ? 'Aturar la preescolta' : 'Escoltar-lo per la preescolta';
    el['preview-label'].textContent = previewing ? ' Aturar' : ' Escoltar';
    setEnabled(el.preview, !!item?.exists && !item?.busy);
    el.edit.classList.toggle('d-none', !pending);
    setEnabled(el.edit, !!item?.exists && !item?.busy);
    el.save.classList.toggle('d-none', !pending);
    el.discard.classList.toggle('d-none', !pending);
    setEnabled(el.save, !!item?.exists && !item?.busy);
    setEnabled(el.discard, !!item?.exists && !item?.busy);

    el.error.textContent = r.error ?? '';
  }

  /** Nivells nous del motor (esdeveniment meters): [esquerre, dret] en dBFS, i quant abaixa el limitador (dB). */
  updateMeter(/** @type {number[] | undefined} */ levels, /** @type {number | undefined} */ reduction) {
    if (levels) this.#meter.update(levels);
    if (reduction !== undefined && !this.#el.gr.classList.contains('d-none')) {
      this.#shownReduction = Math.max(reduction, this.#shownReduction - 0.4);
      this.#el['gr-fill'].style.width = `${Math.min(100, (this.#shownReduction / LIMITER_SCALE_DB) * 100)}%`;
      this.#el['gr-value'].textContent = `${this.#shownReduction.toFixed(1).replace('.', ',')} dB`;
    }
  }

  // -------------------------------------------------------------------------

  /** «312 KB · lliure 131 GB (≈ 910 h)»: mida del fitxer i espai que queda al disc. */
  #sizesText(/** @type {any} */ r) {
    const parts = [];
    if (r.file) parts.push(formatBytes(r.fileSize ?? 0));
    if (r.freeDiskBytes > 0) {
      const bytesPerHour = ((r.bitrateKbps || 320) * 1000 / 8) * 3600;
      const hours = Math.floor(r.freeDiskBytes / bytesPerHour);
      parts.push(`lliure ${formatBytes(r.freeDiskBytes)} (≈ ${hours.toLocaleString('ca-ES')} h)`);
    }
    return escapeHtml(parts.join(' · '));
  }

  /** Envia una ordre; si el motor la rebutja, en mostra el motiu uns segons. */
  async #send(/** @type {string} */ cmd, /** @type {object} */ params = {}) {
    try {
      await this.#request(cmd, { recorder: this.#id, ...params });
      return true;
    } catch (err) {
      this.#showMessage(/** @type {Error} */ (err).message);
      return false;
    }
  }

  async #setDevice(/** @type {string} */ deviceId) {
    if (!(await this.#send('recorder.setDevice', { deviceId }))) {
      this.#deviceSelect.invalidate();
      this.render();
    }
  }

  /**
   * Aturar demana una segona pulsació (especificació 8.3): la primera canvia el botó a
   * «Prem de nou per aturar» uns segons; si no es torna a prémer, torna a l'estat normal.
   */
  #stopPressed() {
    if (this.#stopArmedTimer === undefined) {
      this.#el.stop.classList.add('armed');
      this.#el['stop-label'].textContent = ' Prem de nou per aturar';
      this.#stopArmedTimer = window.setTimeout(() => this.#disarmStop(), STOP_CONFIRM_MS);
      return;
    }
    this.#disarmStop();
    this.#send('recorder.stop');
  }

  #disarmStop() {
    if (this.#stopArmedTimer === undefined) return;
    clearTimeout(this.#stopArmedTimer);
    this.#stopArmedTimer = undefined;
    this.#el.stop.classList.remove('armed');
    this.#el['stop-label'].textContent = ' Aturar';
  }

  async #preview() {
    const item = this.#store.pendingRecording(this.#id);
    if (!item) return;
    const message = await togglePreview(this.#store, this.#request, item.path);
    if (message) this.#showMessage(message);
  }

  async #save() {
    const item = this.#store.pendingRecording(this.#id);
    if (!item) return;
    const message = await saveRecording(item, this.#request);
    if (message) this.#showMessage(message);
  }

  async #discard() {
    const item = this.#store.pendingRecording(this.#id);
    if (!item) return;
    const message = await discardRecording(item, this.#request);
    if (message) this.#showMessage(message);
  }

  async #editSettings() {
    const r = this.#store.recorders.get(this.#id);
    if (!r) return;
    const result = await editRecorderSettings(r);
    if (result) this.#send('recorder.configure', result);
  }

  #showMessage(/** @type {string} */ text) {
    this.#el.message.textContent = text;
    clearTimeout(this.#messageTimer);
    this.#messageTimer = window.setTimeout(() => { this.#el.message.textContent = ''; }, MESSAGE_MS);
  }
}

/** «−1,0»: dB amb un decimal, coma i signe menys de debò. */
function formatDb(/** @type {number} */ db) {
  return db.toFixed(1).replace('.', ',').replace('-', '−');
}

/** @param {HTMLElement} button @param {boolean} enabled */
function setEnabled(button, enabled) {
  /** @type {HTMLButtonElement} */ (button).disabled = !enabled;
}
