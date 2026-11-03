// @ts-check
// Panell d'un reproductor.
// Pas 1: nom, estat, sortida i pista. Els controls (Play, Stop...) arriben al pas 2.

import { PLAYER_STATES, stateBadge } from './states.js';
import { deviceLine } from './device-line.js';
import { escapeHtml, fileName } from './format.js';

/** @typedef {import('../store.js').Store} Store */

export class PlayerPanel {
  /** @type {HTMLElement} */ element;
  #store;
  #id;

  /**
   * @param {Store} store
   * @param {string} id  identificador del reproductor ("1", "2"...)
   */
  constructor(store, id) {
    this.#store = store;
    this.#id = id;

    this.element = document.createElement('section');
    this.element.className = 'card panel player';
    this.element.setAttribute('aria-label', `Reproductor ${id}`);
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-title"><i class="bi bi-music-note-list"></i> Reproductor ${escapeHtml(id)}</span>
        <span class="js-state"></span>
      </div>
      <div class="card-body panel-body">
        <div class="js-device"></div>
        <div class="track js-track"></div>
        <div class="error-text js-error"></div>
        <div class="placeholder-text">Controls: pas 2</div>
      </div>`;

    this.render();
  }

  render() {
    const p = this.#store.players.get(this.#id);
    if (!p) return;

    const $ = (/** @type {string} */ sel) => /** @type {HTMLElement} */ (this.element.querySelector(sel));
    $('.js-state').innerHTML = stateBadge(PLAYER_STATES[p.state]);
    $('.js-device').innerHTML = deviceLine(this.#store, p.deviceId);
    $('.js-track').textContent = p.file ? fileName(p.file) : 'Cap pista carregada';
    $('.js-track').title = p.file ?? '';
    $('.js-error').textContent = p.error ?? '';
  }
}
