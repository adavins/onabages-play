// @ts-check
// Panell d'un enregistrador.
// Pas 1: nom, estat, entrada, fitxer i temps. Els controls (Enregistrar, Aturar...) arriben al pas 6.

import { RECORDER_STATES, stateBadge } from './states.js';
import { deviceLine } from './device-line.js';
import { escapeHtml, fileName, formatDuration } from './format.js';

/** @typedef {import('../store.js').Store} Store */

export class RecorderPanel {
  /** @type {HTMLElement} */ element;
  #store;
  #id;

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
        <div class="track js-file"></div>
        <div class="elapsed js-elapsed"></div>
        <div class="error-text js-error"></div>
      </div>`;

    this.render();
  }

  render() {
    const r = this.#store.recorders.get(this.#id);
    if (!r) return;

    const $ = (/** @type {string} */ sel) => /** @type {HTMLElement} */ (this.element.querySelector(sel));
    $('.js-state').innerHTML = stateBadge(RECORDER_STATES[r.state]);
    $('.js-device').innerHTML = deviceLine(this.#store, r.deviceId);
    $('.js-file').textContent = r.file ? fileName(r.file) : '';
    $('.js-file').title = r.file ?? '';
    $('.js-elapsed').textContent = r.state === 'recording' || r.state === 'pending' ? formatDuration(r.elapsed) : '';
    $('.js-error').textContent = r.error ?? '';
  }
}
