// @ts-check
// Panell «Recents»: enregistraments acabats que ja no són el pendent de cap enregistrador
// (especificació 8.3, docs/protocol.md 4.6). El més nou, a dalt.
// Accions de cada un: Preescoltar, Desar, Mostrar a l'Explorador, Descartar i Treure de la llista.
// «Netejar» (fase 5, D-089) treu els desats i els no trobats; si n'hi ha de no desats, pregunta.

import { saveRecording, discardRecording } from './recording-actions.js';
import { choose } from './dialogs.js';
import { togglePreview, previewButton } from './cue-actions.js';
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
  /** @type {HTMLButtonElement} */ #clear;
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
        <span class="panel-header-right">
          <span class="rc-count js-count"></span>
          <button class="btn btn-sm dsp-btn js-clear" type="button"><i class="bi bi-eraser"></i> Netejar</button>
        </span>
      </div>
      <div class="card-body panel-body rc-body">
        <div class="message-text js-message"></div>
        <div class="rc-list js-list"></div>
      </div>`;

    this.#list = /** @type {HTMLElement} */ (this.element.querySelector('.js-list'));
    this.#count = /** @type {HTMLElement} */ (this.element.querySelector('.js-count'));
    this.#message = /** @type {HTMLElement} */ (this.element.querySelector('.js-message'));
    this.#list.addEventListener('click', (e) => this.#onClick(e));
    this.#clear = /** @type {HTMLButtonElement} */ (this.element.querySelector('.js-clear'));
    this.#clear.addEventListener('click', () => this.#clearList());

    this.render();
  }

  render() {
    const items = this.#store.recordings.filter((i) => !this.#store.isPending(i));

    // Els noms dels enregistradors també surten a la llista: si canvien, cal tornar-la a dibuixar.
    const names = [...this.#store.recorders.values()].map((r) => r.name);
    const cue = this.#store.cue;
    const previewing = cue?.state === 'playing' ? cue.file : null;
    const key = JSON.stringify([items, names, previewing]);
    if (key === this.#key) return;
    this.#key = key;

    this.#count.textContent = items.length > 0 ? String(items.length) : '';
    const { removable, unsaved } = clearable(items);
    this.#clear.disabled = removable + unsaved === 0;
    this.#clear.title = removable + unsaved === 0
      ? 'No hi ha res a netejar'
      : 'Treure de la llista els desats i els no trobats (els fitxers no es toquen). Si n\'hi ha de no desats, es pregunta.';
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
    if (item.recovered) badges.push('<span class="rc-badge rc-recovered" title="L\'enregistrament es va interrompre (el motor o l\'ordinador van caure) i s\'ha recuperat en arrencar: el fitxer és vàlid fins on es va poder desar."><i class="bi bi-bandaid"></i> Recuperat</span>');
    else if (!item.closedCleanly) badges.push('<span class="rc-badge rc-cut" title="L\'enregistrament es va tallar (entrada perduda o error): el fitxer és vàlid fins a aquell moment."><i class="bi bi-scissors"></i> Interromput</span>');

    const meta = [startTime(item.startedAt), formatDuration(item.duration), formatBytes(item.fileSize), recorderName];
    const disabled = (/** @type {boolean} */ off) => (off ? ' disabled' : '');
    const previewing = this.#store.isPreviewing(item.path);

    return `
      <div class="rc-row${item.exists ? '' : ' rc-row-missing'}${previewing ? ' rc-previewing' : ''}" data-id="${escapeHtml(item.id)}">
        <div class="rc-main">
          <div class="rc-name" title="${escapeHtml(item.path)}">${escapeHtml(fileName(item.path))}</div>
          <div class="rc-meta">${escapeHtml(meta.join(' · '))} ${badges.join(' ')}</div>
          ${item.problem ? `<div class="error-text rc-problem">${escapeHtml(item.problem)}</div>` : ''}
        </div>
        <div class="rc-actions">
          ${previewButton(previewing, `data-action="preview"${disabled(!usable)}`, 'rc-btn')}
          <button class="rc-btn" data-action="edit" type="button" title="Editar: obrir-lo a l'editor"${disabled(!usable)}><i class="bi bi-scissors"></i></button>
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
      case 'preview':
        message = await togglePreview(this.#store, this.#request, item.path);
        break;
      case 'save':
        message = await saveRecording(item, this.#request);
        break;
      case 'discard':
        message = await discardRecording(item, this.#request);
        break;
      case 'show':
        bridge?.showInFolder(item.path);
        break;
      case 'edit':
        bridge?.openEditor(item.path); // finestra de l'editor (fase 3)
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

  /**
   * Netejar: els desats i els no trobats es treuen sense preguntar (no es perd res de vista que calgui).
   * Si n'hi ha de no desats, es pregunta: els fitxers no s'esborren, però deixarien de sortir aquí.
   */
  async #clearList() {
    const items = this.#store.recordings.filter((i) => !this.#store.isPending(i));
    const { removable, unsaved } = clearable(items);
    let includeUnsaved = false;
    if (unsaved > 0) {
      const which = unsaved === 1
        ? 'Hi ha 1 enregistrament que encara no s\'ha desat.'
        : `Hi ha ${unsaved} enregistraments que encara no s'han desat.`;
      /** @type {{ label: string, value: string, style?: string }[]} */
      const buttons = [{ label: 'Cancel·lar', value: 'cancel' }];
      if (removable > 0) buttons.push({ label: 'Només els desats', value: 'saved', style: 'primary' });
      buttons.push({ label: 'Treure\'ls tots', value: 'all', style: removable > 0 ? 'outline-warning' : 'warning' });
      const choice = await choose({
        title: 'Netejar Recents',
        icon: 'bi-eraser',
        message: `${which}\nSi els treus de la llista, els fitxers no s'esborren (són a la carpeta de treball i a l'historial d'enregistraments), però ja no sortiran aquí.`,
        buttons,
      });
      if (choice === 'cancel') return;
      includeUnsaved = choice === 'all';
    }
    try {
      await this.#request('recordings.clear', { includeUnsaved });
    } catch (err) {
      this.#showMessage(/** @type {Error} */ (err).message);
    }
  }

  #showMessage(/** @type {string} */ text) {
    this.#message.textContent = text;
    clearTimeout(this.#messageTimer);
    this.#messageTimer = window.setTimeout(() => { this.#message.textContent = ''; }, MESSAGE_MS);
  }
}

/**
 * Què es pot netejar (com ho fa el motor amb recordings.clear): quants són desats o no trobats, i quants
 * no s'han desat. Els que s'estan desant no compten.
 */
function clearable(/** @type {any[]} */ items) {
  const free = items.filter((i) => !i.busy);
  const removable = free.filter((i) => i.saved || !i.exists).length;
  return { removable, unsaved: free.length - removable };
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
