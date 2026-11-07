// @ts-check
// Botonera d'instantanis (fase 5, pas 28c; D-127, D-129): una graella de botons que fan sonar talls a l'instant,
// diversos alhora, per una sortida pròpia. És una pestanya del panell lateral.
// La interfície només envia ordres (cart.*) i mostra l'estat que rep del motor.
//
// - Clic a un botó: si no sona, sona des del principi; si sona, s'atura (amb un fos molt curt).
// - Mentre sona: el botó s'omple d'esquerra a dreta i mostra el temps restant (en vermell els últims segons).
// - Pàgines: 1, 2, 3, 4 (la que es veu es recorda).
// - Teclat numèric (handleKey): 1–9 i 0 fan sonar els 10 primers botons de la pàgina; + i − canvien de pàgina;
//   la coma (o el punt) atura-ho tot.
// - Assignar (pas 28d; D-130): arrossegar àudios a un botó (des d'Àudios, d'una llista o de l'Explorador de Windows).
//   Si se'n deixen anar diversos, omplen aquell botó i els buits següents de la pàgina.
// - Botó dret: Editar… (nom, color, volum, fitxer), Escoltar per la preescolta, Treure; en un de buit, Triar un fitxer….

import { deviceWarning } from './device-line.js';
import { DeviceSelect } from './device-select.js';
import { LevelMeter } from './meter.js';
import { escapeHtml, fileName, formatDuration } from './format.js';
import { draggedPaths, PATHS_DRAG_TYPE } from './drag-types.js';
import { showContextMenu } from './context-menu.js';
import { editCartButton } from './cart-button-dialog.js';
import { togglePreview } from './cue-actions.js';

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

/** Pistes arrossegades des de la llista d'un reproductor (playlist-view.js): porten les rutes. */
const PLAYLIST_DRAG_TYPE = 'application/x-obp-items';

/** Llargada màxima del nom d'un botó (com al motor). */
const MAX_NAME = 40;

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

const PAGE_STORAGE_KEY = 'obp.cartPage';
const MESSAGE_MS = 5000;
const DEFAULT_END_WARNING_SECONDS = 10;

/** Tecla del teclat numèric de cada botó de la pàgina (els 10 primers). */
const KEY_LABELS = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'];

export class CartPanel {
  /** @type {HTMLElement} */ element;
  #store;
  #request;
  #deviceSelect;
  #meter;
  /** @type {Record<string, HTMLElement>} */ #el = {};
  /** @type {HTMLButtonElement[]} */ #buttons = [];
  /** @type {number | undefined} */ #messageTimer;
  #page = 0;
  #gridKey = '';

  /**
   * @param {Store} store
   * @param {Request} request  envia una ordre al motor
   */
  constructor(store, request) {
    this.#store = store;
    this.#request = request;
    this.#page = Math.max(0, Number(localStorage.getItem(PAGE_STORAGE_KEY)) || 0);

    this.element = document.createElement('section');
    this.element.className = 'card panel cart';
    this.element.setAttribute('aria-label', 'Botonera');
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-title"><i class="bi bi-grid-3x3-gap-fill"></i> Botonera</span>
        <span class="js-state"></span>
      </div>
      <div class="card-body panel-body">
        <div class="js-device-slot"></div>
        <div class="js-device-warning"></div>
        <div class="cart-shared js-shared"><i class="bi bi-info-circle"></i> Surt per la mateixa sortida que un reproductor</div>
        <div class="cart-pages js-pages" role="tablist" aria-label="Pàgines de la botonera"></div>
        <div class="cart-grid js-grid"></div>
        <div class="cart-hint js-hint"><i class="bi bi-hand-index"></i> Arrossega àudios als botons (des d'Àudios, d'una llista o de l'Explorador de Windows). Botó dret: editar.</div>
        <div class="cart-bottom">
          <div class="js-meter-slot cart-meter"></div>
          <button class="btn btn-sm btn-outline-danger js-stop-all" type="button" title="Aturar tot el que sona a la botonera (coma del teclat numèric)">
            <i class="bi bi-stop-fill"></i> Aturar-ho tot
          </button>
        </div>
        <div class="error-text js-error"></div>
        <div class="message-text js-message"></div>
      </div>`;

    for (const node of this.element.querySelectorAll('[class*="js-"]')) {
      const name = [...node.classList].find((c) => c.startsWith('js-'));
      if (name) this.#el[name.slice(3)] = /** @type {HTMLElement} */ (node);
    }

    this.#deviceSelect = new DeviceSelect('output', (deviceId) => this.#setDevice(deviceId));
    this.#el['device-slot'].appendChild(this.#deviceSelect.element);
    this.#meter = new LevelMeter();
    this.#el['meter-slot'].appendChild(this.#meter.element);

    this.#el['stop-all'].addEventListener('click', () => this.#send('cart.stopAll'));
    this.#el.pages.addEventListener('click', (e) => {
      const tab = /** @type {HTMLElement} */ (e.target).closest('[data-page]');
      if (tab) this.#showPage(Number(/** @type {HTMLElement} */ (tab).dataset.page));
    });
    this.#el.grid.addEventListener('click', (e) => {
      const button = /** @type {HTMLElement} */ (e.target).closest('[data-index]');
      if (button) this.#press(Number(/** @type {HTMLElement} */ (button).dataset.index));
    });
    this.#el.grid.addEventListener('contextmenu', (e) => {
      const button = /** @type {HTMLElement} */ (e.target).closest('[data-index]');
      if (button) this.#menu(e, Number(/** @type {HTMLElement} */ (button).dataset.index));
    });
    this.#setupDrop();

    this.render();
  }

  /** Hi sona alguna cosa? (per a la icona del panell lateral). */
  get playing() {
    return (this.#store.cart?.playing?.length ?? 0) > 0;
  }

  render() {
    const c = this.#store.cart;
    if (!c) return;
    const el = this.#el;
    const pages = c.pages ?? 4;
    if (this.#page >= pages) this.#page = 0;

    // Capçalera: quants en sonen (color + icona + text).
    const count = c.playing?.length ?? 0;
    el.state.innerHTML = count > 0
      ? `<span class="state state-cart"><i class="bi bi-volume-up-fill"></i> ${count === 1 ? 'Sona' : `Sonen ${count}`}</span>`
      : !c.deviceId
        ? '<span class="state state-idle"><i class="bi bi-exclamation-circle"></i> Sense sortida</span>'
        : c.outputReady ? '' : '<span class="state state-error"><i class="bi bi-exclamation-triangle-fill"></i> Error</span>';
    this.element.classList.toggle('cart-playing', count > 0);

    this.#deviceSelect.update(this.#store, c.deviceId, false);
    el['device-warning'].innerHTML = deviceWarning(this.#store, c.deviceId);
    el.shared.classList.toggle('d-none', !this.#store.cartSharesPlayerOutput);
    el.error.textContent = c.error ?? '';
    el['stop-all'].toggleAttribute('disabled', count === 0);

    // Pàgines: una pestanya per pàgina; les que tenen algun botó que sona porten un punt.
    const playingPages = new Set((c.playing ?? []).map((/** @type {any} */ p) => p.page));
    el.pages.innerHTML = Array.from({ length: pages }, (_, i) => `
      <button type="button" class="btn btn-sm cart-page${i === this.#page ? ' active' : ''}" data-page="${i}" role="tab"
        aria-selected="${i === this.#page}" title="Pàgina ${i + 1} (+ i − del teclat numèric)">${i + 1}${playingPages.has(i) && i !== this.#page ? ' <i class="bi bi-volume-up-fill"></i>' : ''}</button>`).join('');

    this.#renderGrid();
    this.renderPlaying();
    el.hint.classList.toggle('d-none', (c.buttons?.length ?? 0) > 0);
  }

  /** Construeix la graella de la pàgina (només quan canvien els botons o la pàgina; la posició va a renderPlaying). */
  #renderGrid() {
    const c = this.#store.cart;
    const total = (c.rows ?? 4) * (c.columns ?? 4);
    const buttons = (c.buttons ?? []).filter((/** @type {any} */ b) => b.page === this.#page);
    const key = JSON.stringify([this.#page, total, buttons]);
    if (key === this.#gridKey) return;
    this.#gridKey = key;

    const grid = this.#el.grid;
    grid.style.setProperty('--cart-columns', String(c.columns ?? 4));
    this.#buttons = Array.from({ length: total }, (_, index) => {
      const b = buttons.find((/** @type {any} */ x) => x.index === index);
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'cart-btn';
      button.dataset.index = String(index);
      const keyLabel = KEY_LABELS[index] ? `<span class="cart-key">${KEY_LABELS[index]}</span>` : '';
      if (!b) {
        button.classList.add('cart-empty');
        button.setAttribute('aria-label', `Botó ${index + 1}, buit`);
        button.innerHTML = keyLabel;
        button.title = 'Botó buit: arrossega-hi un àudio, o botó dret per triar-ne un';
        return button;
      }
      if (b.color) button.style.setProperty('--cart-color', b.color);
      const problem = b.status === 'missing' || b.status === 'error';
      button.classList.toggle('cart-problem', problem);
      const icon = problem ? '<i class="bi bi-exclamation-triangle-fill"></i> '
        : b.status === 'loading' ? '<i class="bi bi-hourglass-split"></i> ' : '';
      button.innerHTML = `
        <span class="cart-fill"></span>
        ${keyLabel}
        <span class="cart-name">${icon}${escapeHtml(b.name)}</span>
        <span class="cart-time">${b.duration ? formatDuration(b.duration) : ''}</span>`;
      button.title = `${b.name}\n${b.path}${problem ? `\n⚠ ${b.error ?? 'No es pot llegir el fitxer.'}` : ''}${b.gainDb ? `\nVolum: ${b.gainDb > 0 ? '+' : ''}${b.gainDb} dB` : ''}`;
      return button;
    });
    grid.replaceChildren(...this.#buttons);
  }

  /** Què sona: el botó s'omple i mostra el temps restant. Es crida 10 vegades per segon mentre sona algun. */
  renderPlaying() {
    const c = this.#store.cart;
    if (!c) return;
    const warning = this.#store.settings?.ui?.endWarningSeconds ?? DEFAULT_END_WARNING_SECONDS;
    /** @type {Map<number, any>} */
    const playing = new Map();
    for (const p of c.playing ?? []) {
      if (p.page === this.#page && !playing.has(p.index)) playing.set(p.index, p);
    }
    for (const [index, button] of this.#buttons.entries()) {
      const p = playing.get(index);
      button.classList.toggle('cart-on', !!p);
      const fill = /** @type {HTMLElement | null} */ (button.querySelector('.cart-fill'));
      const time = /** @type {HTMLElement | null} */ (button.querySelector('.cart-time'));
      const b = (c.buttons ?? []).find((/** @type {any} */ x) => x.page === this.#page && x.index === index);
      if (!fill || !time || !b) continue;
      if (p && p.duration > 0) {
        const left = Math.max(0, p.duration - p.position);
        fill.style.width = `${Math.min(100, (p.position / p.duration) * 100)}%`;
        time.textContent = `−${formatDuration(Math.ceil(left))}`;
        button.classList.toggle('cart-ending', warning > 0 && left <= warning);
      } else {
        fill.style.width = '0';
        time.textContent = b.duration ? formatDuration(b.duration) : '';
        button.classList.remove('cart-ending');
      }
    }
  }

  /** Nivells nous del motor (esdeveniment meters): [esquerre, dret] en dBFS. */
  updateMeter(/** @type {number[] | undefined} */ levels) {
    if (levels) this.#meter.update(levels);
  }

  /**
   * Tecles del teclat numèric (les crida app.js). Retorna true si la tecla era de la botonera.
   * @param {KeyboardEvent} e
   */
  handleKey(e) {
    const c = this.#store.cart;
    if (!c) return false;
    const digit = /^Numpad([0-9])$/.exec(e.code);
    if (digit) {
      const n = Number(digit[1]);
      this.#press(n === 0 ? 9 : n - 1);
      return true;
    }
    if (e.code === 'NumpadAdd') { this.#showPage((this.#page + 1) % (c.pages ?? 4)); return true; }
    if (e.code === 'NumpadSubtract') { this.#showPage((this.#page + (c.pages ?? 4) - 1) % (c.pages ?? 4)); return true; }
    if (e.code === 'NumpadDecimal') { this.#send('cart.stopAll'); return true; }
    return false;
  }

  // -------------------------------------------------------------------------

  /** El botó assignat d'aquesta pàgina, si n'hi ha. */
  #buttonAt(/** @type {number} */ index) {
    return (this.#store.cart?.buttons ?? []).find((/** @type {any} */ x) => x.page === this.#page && x.index === index);
  }

  /** Botó dret: el menú d'un botó assignat o d'un de buit. */
  #menu(/** @type {MouseEvent} */ e, /** @type {number} */ index) {
    const b = this.#buttonAt(index);
    if (!b) {
      showContextMenu(e, [{ label: 'Triar un fitxer…', icon: 'bi-folder2-open', action: () => this.#chooseFile(index) }]);
      return;
    }
    const previewing = this.#store.isPreviewing(b.path);
    showContextMenu(e, [
      { label: 'Editar…', icon: 'bi-pencil', action: () => this.#edit(index) },
      { label: previewing ? 'Aturar la preescolta' : 'Escoltar per la preescolta', icon: 'bi-headphones', action: () => this.#preview(b.path) },
      { separator: true },
      { label: 'Treure el fitxer del botó', icon: 'bi-x-lg', action: () => this.#clear(index) },
    ]);
  }

  async #chooseFile(/** @type {number} */ index) {
    const [path] = (await bridge?.openAudioFiles({ title: `Fitxer del botó ${index + 1}`, single: true })) ?? [];
    if (path) this.#assign(index, [path]);
  }

  async #edit(/** @type {number} */ index) {
    const b = this.#buttonAt(index);
    const c = this.#store.cart;
    if (!b || !c) return;
    const page = this.#page;
    const changes = await editCartButton(b, { minGainDb: c.minGainDb ?? -30, maxGainDb: c.maxGainDb ?? 10, maxName: MAX_NAME });
    if (!changes) return;
    // Un fitxer nou substitueix el botó (el motor el torna a llegir); el nom, el color i el volum es tornen a posar.
    if (changes.path !== b.path && !(await this.#send('cart.assign', { page, index, path: changes.path, name: changes.name || undefined }))) return;
    await this.#send('cart.configure', {
      page, index, color: changes.color, gainDb: changes.gainDb,
      ...(changes.name ? { name: changes.name } : {}),
    });
  }

  async #preview(/** @type {string} */ path) {
    const error = await togglePreview(this.#store, this.#request, path);
    if (error) this.#showMessage(error);
  }

  async #clear(/** @type {number} */ index) {
    const b = this.#buttonAt(index);
    if (b && await this.#send('cart.clear', { page: this.#page, index })) this.#showMessage(`S'ha buidat el botó «${b.name}».`);
  }

  /**
   * Assigna fitxers a partir d'un botó: el primer, al botó; la resta, als buits següents de la pàgina.
   * Si n'hi ha més que buits, els que sobren es diuen.
   */
  async #assign(/** @type {number} */ index, /** @type {string[]} */ paths) {
    const c = this.#store.cart;
    if (!c || paths.length === 0) return;
    const total = (c.rows ?? 4) * (c.columns ?? 4);
    const page = this.#page;
    const targets = [index];
    for (let i = index + 1; i < total && targets.length < paths.length; i++) {
      if (!this.#buttonAt(i)) targets.push(i);
    }
    for (const [n, target] of targets.entries()) {
      if (!(await this.#send('cart.assign', { page, index: target, path: paths[n] }))) return;
    }
    const left = paths.length - targets.length;
    if (left > 0) this.#showMessage(`No hi ha prou botons buits en aquesta pàgina: ${left} fitxer${left === 1 ? '' : 's'} sense botó (${paths.slice(targets.length).map(fileName).join(', ')}).`);
  }

  /** Arrossegar àudios als botons. */
  #setupDrop() {
    const grid = this.#el.grid;
    const accepts = (/** @type {DragEvent} */ e) => {
      const types = e.dataTransfer?.types ?? [];
      return types.includes('Files') || types.includes(PATHS_DRAG_TYPE) || types.includes(PLAYLIST_DRAG_TYPE);
    };
    const target = (/** @type {DragEvent} */ e) => /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('[data-index]'));
    const clearMark = () => grid.querySelectorAll('.cart-drop').forEach((el) => el.classList.remove('cart-drop'));

    grid.addEventListener('dragover', (e) => {
      if (!accepts(e) || !target(e)) return;
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
      const t = target(e);
      if (!t?.classList.contains('cart-drop')) {
        clearMark();
        t?.classList.add('cart-drop');
      }
    });
    grid.addEventListener('dragleave', (e) => {
      if (!grid.contains(/** @type {Node | null} */ (e.relatedTarget))) clearMark();
    });
    grid.addEventListener('drop', (e) => {
      const t = target(e);
      clearMark();
      if (!t || !e.dataTransfer) return;
      e.preventDefault();
      // Des d'Àudios (rutes), d'una llista (pistes amb la ruta) o de l'Explorador de Windows (fitxers).
      let paths = draggedPaths(e.dataTransfer);
      if (paths.length === 0 && e.dataTransfer.types.includes(PLAYLIST_DRAG_TYPE)) {
        try { paths = JSON.parse(e.dataTransfer.getData(PLAYLIST_DRAG_TYPE)).paths ?? []; } catch { paths = []; }
      }
      if (paths.length === 0) paths = [...e.dataTransfer.files].map((f) => bridge?.pathForFile(f)).filter(Boolean);
      this.#assign(Number(t.dataset.index), paths.filter((p) => typeof p === 'string' && p));
    });
  }

  #showPage(/** @type {number} */ page) {
    this.#page = page;
    localStorage.setItem(PAGE_STORAGE_KEY, String(page));
    this.render();
  }

  #press(/** @type {number} */ index) {
    const b = (this.#store.cart?.buttons ?? []).find((/** @type {any} */ x) => x.page === this.#page && x.index === index);
    if (!b) return;
    this.#send('cart.press', { page: this.#page, index });
  }

  async #send(/** @type {string} */ cmd, /** @type {object} */ params = {}) {
    try {
      await this.#request(cmd, params);
      return true;
    } catch (err) {
      this.#showMessage(/** @type {Error} */ (err).message);
      return false;
    }
  }

  async #setDevice(/** @type {string} */ deviceId) {
    if (!(await this.#send('cart.setDevice', { deviceId }))) {
      this.#deviceSelect.invalidate();
      this.render();
    }
  }

  #showMessage(/** @type {string} */ text) {
    this.#el.message.textContent = text;
    clearTimeout(this.#messageTimer);
    this.#messageTimer = window.setTimeout(() => { this.#el.message.textContent = ''; }, MESSAGE_MS);
  }
}
