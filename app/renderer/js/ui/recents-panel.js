// @ts-check
// Panell «Recents»: enregistraments acabats que ja no són el pendent de cap enregistrador
// (especificació 8.3, docs/protocol.md 4.6). El més nou, a dalt.
// Accions de cada un: Desar, Mostrar a l'Explorador, Descartar i Treure de la llista.

import { saveRecording, discardRecording } from './recording-actions.js';
import { escapeHtml, fileName, formatBytes, formatDuration } from './format.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** Quant temps es mostra un missatge (error d'una ordre, avís en desar...). */
const MESSAGE_MS = 6000;

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

export class RecentsPanel {
  /** @type {HTMLElement} */ element;
  #store;
  #request;
  /** @type {HTMLElement} */ #list;
  /** @type {HTMLElement} */ #count;
  /** @type {HTMLElement} */ #message;
  /** @type {number | undefined} */ #messageTimer;
  /** Què es va dibuixar l'última vegada: si no ha canviat, no es torna a dibuixar. */
  #key = '';

  /**
   * @param {Store} store
   * @param {Request} request  envia una ordre al motor
   */
  constructor(store, request) {
    this.#store = store;
    this.#request = request;

    this.element = document.createElement('section');
    this.element.className = 'card panel recents';
    this.element.setAttribute('aria-label', 'Enregistraments recents');
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-title"><i class="bi bi-clock-history"></i> Recents</span>
        <span class="rc-count js-count"></span>
      </div>
      <div class="card-body panel-body rc-body">
        <div class="message-text js-message"></div>
        <div class="rc-list js-list"></div>
      </div>`;

    this.#list = /** @type {HTMLElement} */ (this.element.querySelector('.js-list'));
    this.#count = /** @type {HTMLElement} */ (this.element.querySelector('.js-count'));
    this.#message = /** @type {HTMLElement} */ (this.element.querySelector('.js-message'));
    this.#list.addEventListener('click', (e) => this.#onClick(e));

    this.render();
  }

  render() {
    const items = this.#store.recordings.filter((i) => !this.#store.isPending(i));

    // Els noms dels enregistradors també surten a la llista: si canvien, cal tornar-la a dibuixar.
    const names = [...this.#store.recorders.values()].map((r) => r.name);
    const key = JSON.stringify([items, names]);
    if (key === this.#key) return;
    this.#key = key;

    this.#count.textContent = items.length > 0 ? String(items.length) : '';
    if (items.length === 0) {
      this.#list.innerHTML = '<div class="pl-empty"><i class="bi bi-inbox"></i> Encara no hi ha enregistraments acabats.</div>';
      return;
    }
    this.#list.innerHTML = items.map((i) => this.#row(i)).join('');
  }

  // -------------------------------------------------------------------------

  /** Una fila: nom del fitxer; a sota, hora, durada, mida, enregistrador i avisos; i els botons. */
  #row(/** @type {any} */ item) {
    const usable = item.exists && !item.busy;
    const recorder = this.#store.recorders.get(item.recorder);
    const recorderName = recorder?.name ?? `Enregistrador ${item.recorder}`;

    // Avisos: sempre icona + text (no només color).
    const badges = [];
    if (item.busy) badges.push('<span class="rc-badge rc-busy"><i class="bi bi-arrow-repeat"></i> Movent…</span>');
    else if (!item.exists) badges.push('<span class="rc-badge rc-missing"><i class="bi bi-exclamation-triangle-fill"></i> No trobat</span>');
    if (item.saved) badges.push('<span class="rc-badge rc-saved"><i class="bi bi-check2"></i> Desat</span>');
    if (!item.closedCleanly) badges.push('<span class="rc-badge rc-cut" title="L\'enregistrament es va tallar (entrada perduda o error): el fitxer és vàlid fins a aquell moment."><i class="bi bi-scissors"></i> Interromput</span>');

    const meta = [startTime(item.startedAt), formatDuration(item.duration), formatBytes(item.fileSize), recorderName];
    const disabled = (/** @type {boolean} */ off) => (off ? ' disabled' : '');

    return `
      <div class="rc-row${item.exists ? '' : ' rc-row-missing'}" data-id="${escapeHtml(item.id)}">
        <div class="rc-main">
          <div class="rc-name" title="${escapeHtml(item.path)}">${escapeHtml(fileName(item.path))}</div>
          <div class="rc-meta">${escapeHtml(meta.join(' · '))} ${badges.join(' ')}</div>
          ${item.problem ? `<div class="error-text rc-problem">${escapeHtml(item.problem)}</div>` : ''}
        </div>
        <div class="rc-actions">
          <button class="rc-btn" data-action="save" type="button" title="Desar: triar el nom i la carpeta"${disabled(!usable)}><i class="bi bi-floppy"></i></button>
          <button class="rc-btn" data-action="show" type="button" title="Mostrar a l'Explorador"${disabled(!usable)}><i class="bi bi-folder2-open"></i></button>
          <button class="rc-btn rc-btn-danger" data-action="discard" type="button" title="Descartar: enviar a la paperera"${disabled(!usable)}><i class="bi bi-trash3"></i></button>
          <button class="rc-btn" data-action="remove" type="button" title="Treure de la llista (el fitxer no es toca)"${disabled(item.busy)}><i class="bi bi-x-lg"></i></button>
        </div>
      </div>`;
  }

  /** Un sol escoltador per a tots els botons de la llista. */
  async #onClick(/** @type {MouseEvent} */ e) {
    const button = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('[data-action]'));
    const row = /** @type {HTMLElement | null} */ (button?.closest('[data-id]'));
    if (!button || !row) return;
    const item = this.#store.recordings.find((i) => i.id === row.dataset.id);
    if (!item) return;

    let message = null;
    switch (button.dataset.action) {
      case 'save':
        message = await saveRecording(item, this.#request);
        break;
      case 'discard':
        message = await discardRecording(item, this.#request);
        break;
      case 'show':
        bridge?.showInFolder(item.path);
        break;
      case 'remove':
        try {
          await this.#request('recording.remove', { recordingId: item.id });
        } catch (err) {
          message = /** @type {Error} */ (err).message;
        }
        break;
    }
    if (message) this.#showMessage(message);
  }

  #showMessage(/** @type {string} */ text) {
    this.#message.textContent = text;
    clearTimeout(this.#messageTimer);
    this.#messageTimer = window.setTimeout(() => { this.#message.textContent = ''; }, MESSAGE_MS);
  }
}

/** Hora d'inici: «14:22» si és d'avui; si no, «03/11 14:22». */
function startTime(/** @type {string} */ iso) {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const time = date.toLocaleTimeString('ca-ES', { hour: '2-digit', minute: '2-digit', hour12: false });
  if (date.toDateString() === new Date().toDateString()) return time;
  const day = date.toLocaleDateString('ca-ES', { day: '2-digit', month: '2-digit' });
  return `${day} ${time}`;
}
