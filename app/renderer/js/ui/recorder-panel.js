// @ts-check
// Panell d'un enregistrador.
// Pas 3: nom, estat, entrada, mesurador (sempre actiu), fitxer i temps.
// Els controls (Enregistrar, Aturar...) arriben al pas 6.

import { RECORDER_STATES, stateBadge } from './states.js';
import { deviceLine } from './device-line.js';
import { LevelMeter } from './meter.js';
import { escapeHtml, fileName, formatDuration } from './format.js';

/** @typedef {import('../store.js').Store} Store */

export class RecorderPanel {
  /** @type {HTMLElement} */ element;
  #store;
  #id;
  #meter;

  /**
   * @param {Store} store
   * @param {string} id  identificador de l'enregistrador ("1", "2"...)
   */
  constructor(store, id) {
    this.#store = store;
    this.#id = id;

    this.element = document.createElement('section');
    this.element.className = 'card panel recorder';
    this.element.setAttribute('aria-label', `Enregistrador ${id}`);
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-title"><i class="bi bi-record-circle"></i> Enregistrador ${escapeHtml(id)}</span>
        <span class="js-state"></span>
      </div>
      <div class="card-body panel-body">
        <div class="js-device"></div>
        <div class="js-meter-slot"></div>
        <div class="input-off js-input-off">Entrada no disponible: el mesurador s'activarà quan torni.</div>
        <div class="track js-file"></div>
        <div class="elapsed js-elapsed"></div>
        <div class="error-text js-error"></div>
      </div>`;

    this.#meter = new LevelMeter();
    this.#$('.js-meter-slot').appendChild(this.#meter.element);
    this.render();
  }

  render() {
    const r = this.#store.recorders.get(this.#id);
    if (!r) return;

    this.#$('.js-state').innerHTML = stateBadge(RECORDER_STATES[r.state]);
    this.#$('.js-device').innerHTML = deviceLine(this.#store, r.deviceId);
    this.#$('.js-file').textContent = r.file ? fileName(r.file) : '';
    this.#$('.js-file').title = r.file ?? '';
    this.#$('.js-elapsed').textContent = r.state === 'recording' || r.state === 'pending' ? formatDuration(r.elapsed) : '';
    this.#$('.js-error').textContent = r.error ?? '';

    // Amb dispositiu però sense entrada oberta, el mesurador no pot funcionar: es diu clarament.
    const inputOff = !!r.deviceId && !r.inputActive;
    this.#meter.setActive(!inputOff);
    this.#$('.js-input-off').classList.toggle('d-none', !inputOff);
  }

  /** Nivells nous del motor (esdeveniment meters): [esquerre, dret] en dBFS. */
  updateMeter(/** @type {number[] | undefined} */ levels) {
    if (levels) this.#meter.update(levels);
  }

  #$(/** @type {string} */ selector) {
    return /** @type {HTMLElement} */ (this.element.querySelector(selector));
  }
}
