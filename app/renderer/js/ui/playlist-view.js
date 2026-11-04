// @ts-check
// Llista de reproducció d'un reproductor (especificació 6.3–6.4, D-031).
//
// La llista és al motor: aquí només es mostra i s'envien ordres (playlist.*, player.playItem).
// El que és només de la pantalla (quines pistes estan seleccionades) es guarda aquí.
//
// - Un clic selecciona; Ctrl+clic i Maj+clic, diverses. Supr esborra les seleccionades.
// - Doble clic: fa sonar la pista.
// - Arrossegar: fitxers de l'Explorador (s'insereixen on es deixen anar) o pistes de la
//   llista (es mouen, també d'una llista a una altra; amb Ctrl, es copien).
// - Teclat: Supr, ↑/↓ (Maj: ampliar la selecció), C cue, D duplicar, B bucle, A avanç automàtic.
// - Botó dret: informació i «Mostrar a l'Explorador».

import { escapeHtml, formatDuration } from './format.js';
import { showContextMenu } from './context-menu.js';
import { choose, showInfo } from './dialogs.js';

/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** Tipus de dades d'un arrossegament de pistes entre llistes (no de l'Explorador). */
const DRAG_TYPE = 'application/x-obp-items';

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

export class PlaylistView {
  /** @type {HTMLElement} */ element;
  #playerId;
  #request;
  #onMessage;
  /** @type {HTMLElement} */ #rows;
  /** @type {HTMLElement} */ #footer;
  /** @type {any} */ #playlist = { items: [], cueId: null, totalDuration: 0 };
  /** @type {any} */ #player = {};
  /** Pistes seleccionades (identificadors). */
  #selected = new Set();
  /** Última pista clicada sense Maj (punt de partida de Maj+clic). @type {string | null} */
  #anchor = null;
  /** Pista on és ara el cursor del teclat (↑/↓). @type {string | null} */
  #lead = null;
  #renderKey = '';
  #dragging = false;
  #pendingRender = false;

  /**
   * @param {string} playerId
   * @param {Request} request  envia una ordre al motor
   * @param {(text: string) => void} onMessage  mostra un avís breu al panell (p. ex. si el motor rebutja una ordre)
   */
  constructor(playerId, request, onMessage) {
    this.#playerId = playerId;
    this.#request = request;
    this.#onMessage = onMessage;

    this.element = document.createElement('div');
    this.element.className = 'playlist';
    this.element.innerHTML = `
      <div class="pl-rows" tabindex="0" aria-label="Llista de reproducció"></div>
      <div class="pl-footer">
        <button class="btn btn-sm btn-link pl-action js-open-list" type="button" title="Obrir una llista desada (.obpl) o un M3U">
          <i class="bi bi-folder2-open"></i><span class="pl-action-label"> Obrir llista</span>
        </button>
        <button class="btn btn-sm btn-link pl-action js-save-list" type="button" title="Desar aquesta llista">
          <i class="bi bi-save"></i><span class="pl-action-label"> Desar llista</span>
        </button>
        <button class="btn btn-sm btn-link pl-action js-clear-list" type="button" title="Treure totes les pistes de la llista">
          <i class="bi bi-trash3"></i><span class="pl-action-label"> Netejar llista</span>
        </button>
        <span class="pl-summary"></span>
      </div>`;
    this.#rows = /** @type {HTMLElement} */ (this.element.querySelector('.pl-rows'));
    this.#footer = /** @type {HTMLElement} */ (this.element.querySelector('.pl-summary'));
    /** @type {HTMLElement} */ (this.element.querySelector('.js-open-list')).addEventListener('click', () => this.#openList());
    /** @type {HTMLElement} */ (this.element.querySelector('.js-save-list')).addEventListener('click', () => this.#saveList());
    /** @type {HTMLElement} */ (this.element.querySelector('.js-clear-list')).addEventListener('click', () => this.#clearList());

    this.#rows.addEventListener('click', (e) => this.#onClick(/** @type {MouseEvent} */ (e)));
    this.#rows.addEventListener('dblclick', (e) => this.#onDoubleClick(/** @type {MouseEvent} */ (e)));
    this.#rows.addEventListener('contextmenu', (e) => this.#onContextMenu(/** @type {MouseEvent} */ (e)));
    this.#rows.addEventListener('keydown', (e) => this.#onKey(e));
    this.#setupDrag();
  }

  /**
   * Es crida quan canvia la llista o el reproductor. Només es redibuixa si ha canviat
   * alguna cosa que es veu (no a cada actualització de la posició, 10 per segon).
   * @param {any} playlist  objecte playlist del motor
   * @param {any} player    objecte player del motor
   */
  update(playlist, player) {
    if (playlist) this.#playlist = playlist;
    if (player) this.#player = player;

    // Si s'han esborrat pistes seleccionades, ja no ho estan.
    const ids = new Set(this.#playlist.items.map((/** @type {any} */ i) => i.id));
    for (const id of this.#selected) if (!ids.has(id)) this.#selected.delete(id);

    const key = JSON.stringify([this.#playlist, this.#player.currentItemId, this.#player.lastPlayedId, this.#player.state]);
    if (key !== this.#renderKey) {
      // Mentre s'arrossega no es redibuixa (el navegador cancel·laria l'arrossegament): es fa en acabar.
      if (this.#dragging) {
        this.#pendingRender = true;
        return;
      }
      this.#renderKey = key;
      this.#render();
    }
    this.#applySelection();
  }

  // -------------------------------------------------------------------------
  // Dibuix
  // -------------------------------------------------------------------------

  #render() {
    const { items, cueId, totalDuration } = this.#playlist;
    const playing = this.#player.state === 'playing' || this.#player.state === 'paused';
    const current = this.#player.currentItemId;
    const last = this.#player.lastPlayedId;

    this.#rows.innerHTML = items.length === 0
      ? '<div class="pl-empty"><i class="bi bi-box-arrow-in-down"></i> Arrossega fitxers aquí o prem «Afegir»</div>'
      : items.map((/** @type {any} */ item, /** @type {number} */ index) => {
        const classes = ['pl-row'];
        if (item.id === current && playing) classes.push('pl-playing');
        else if (item.id === current) classes.push('pl-current');
        if (item.id === last && item.id !== current) classes.push('pl-last');
        if (item.status === 'error' || item.status === 'missing') classes.push('pl-problem');
        if (item.status === 'analyzing') classes.push('pl-analyzing');

        const status = item.status === 'error' || item.status === 'missing'
          ? `<span class="pl-warn" title="${escapeHtml(item.problem ?? 'Problema amb el fitxer')}"><i class="bi bi-exclamation-triangle-fill"></i></span>`
          : item.status === 'analyzing'
            ? '<span class="pl-wait" title="Comprovant el fitxer…"><i class="bi bi-hourglass-split"></i></span>'
            : '';

        return `
          <div class="${classes.join(' ')}" data-id="${escapeHtml(item.id)}" draggable="true" title="${escapeHtml(item.path)}">
            ${flag('loop', item.loop, 'bi-repeat', 'Bucle: la pista es repeteix fins a Stop')}
            <span class="pl-num">${index + 1}</span>
            <span class="pl-name">${escapeHtml(item.name)}</span>
            ${status}
            ${flag('cue', item.id === cueId, 'bi-flag-fill', 'Cue: serà la següent a sonar')}
            ${flag('auto', item.autoNext, 'bi-skip-forward-fill', 'Avanç automàtic: en acabar, sona la següent (o la del cue)',
              item.loop ? 'Avanç automàtic: no disponible amb el bucle activat' : null)}
            <span class="pl-dur">${item.duration != null ? formatDuration(item.duration) : ''}</span>
          </div>`;
      }).join('');

    const count = items.length;
    const unknown = items.filter((/** @type {any} */ i) => i.status === 'analyzing').length;
    const problems = items.filter((/** @type {any} */ i) => i.status === 'error' || i.status === 'missing').length;
    this.#footer.textContent = count === 0 ? '' :
      `${count} ${count === 1 ? 'pista' : 'pistes'} · ${formatDuration(totalDuration)}` +
      (unknown > 0 ? ' · comprovant…' : '') +
      (problems > 0 ? ` · ⚠ ${problems} amb problemes` : '');
  }

  /** Marca les files seleccionades (només canvia classes: no redibuixa la llista). */
  #applySelection() {
    for (const row of /** @type {NodeListOf<HTMLElement>} */ (this.#rows.querySelectorAll('.pl-row')))
      row.classList.toggle('pl-selected', this.#selected.has(/** @type {string} */ (row.dataset.id)));
  }

  /** S'ha acabat d'arrossegar: si mentrestant ha canviat la llista, ara es redibuixa. */
  #endDrag() {
    this.#dragging = false;
    this.#showDropMark(null);
    if (this.#pendingRender) {
      this.#pendingRender = false;
      this.#renderKey = '';
      this.update(null, null);
    }
  }

  // -------------------------------------------------------------------------
  // Ratolí i teclat
  // -------------------------------------------------------------------------

  /** @param {MouseEvent} e */
  #onClick(e) {
    const target = /** @type {HTMLElement} */ (e.target);
    const row = /** @type {HTMLElement | null} */ (target.closest('.pl-row'));
    if (!row) return;
    const id = /** @type {string} */ (row.dataset.id);

    // Botons de bucle, cue i avanç automàtic.
    const flagButton = /** @type {HTMLElement | null} */ (target.closest('.pl-flag'));
    if (flagButton) {
      this.#toggleFlag(id, /** @type {string} */ (flagButton.dataset.flag));
      return;
    }

    // Selecció (com a l'Explorador).
    if (e.shiftKey && this.#anchor) {
      this.#selectRange(this.#anchor, id, e.ctrlKey);
    } else if (e.ctrlKey) {
      if (this.#selected.has(id)) this.#selected.delete(id);
      else this.#selected.add(id);
      this.#anchor = id;
    } else {
      this.#selected = new Set([id]);
      this.#anchor = id;
    }
    this.#lead = id;
    this.update(null, null);
  }

  /** Selecciona de <from> a <to> (ambdues incloses). Amb <add>, s'afegeix al que ja hi havia. */
  #selectRange(/** @type {string} */ from, /** @type {string} */ to, add = false) {
    const order = this.#ids();
    const [a, b] = [order.indexOf(from), order.indexOf(to)].sort((x, y) => x - y);
    if (!add) this.#selected.clear();
    for (const sid of order.slice(Math.max(0, a), b + 1)) this.#selected.add(sid);
  }

  /** ↑/↓: mou la selecció; amb Maj, l'amplia des de la pista on s'ha començat. */
  #moveSelection(/** @type {number} */ direction, /** @type {boolean} */ extend) {
    const order = this.#ids();
    if (order.length === 0) return;
    const from = order.indexOf(this.#lead ?? '');
    const index = from < 0 ? (direction > 0 ? 0 : order.length - 1) : Math.min(order.length - 1, Math.max(0, from + direction));
    this.#lead = order[index];

    if (extend && this.#anchor && order.includes(this.#anchor)) {
      this.#selectRange(this.#anchor, this.#lead);
    } else {
      this.#selected = new Set([this.#lead]);
      this.#anchor = this.#lead;
    }
    this.update(null, null);
    this.#rows.querySelector(`[data-id="${CSS.escape(this.#lead)}"]`)?.scrollIntoView({ block: 'nearest' });
  }

  /** Identificadors de la llista, en ordre. */
  #ids() {
    return this.#playlist.items.map((/** @type {any} */ i) => i.id);
  }

  /** Pistes seleccionades, en l'ordre de la llista. */
  #selectedItems() {
    return this.#playlist.items.filter((/** @type {any} */ i) => this.#selected.has(i.id));
  }

  /** @param {MouseEvent} e */
  #onDoubleClick(e) {
    const target = /** @type {HTMLElement} */ (e.target);
    if (target.closest('.pl-flag')) return;
    const row = /** @type {HTMLElement | null} */ (target.closest('.pl-row'));
    if (row) this.#send('player.playItem', { player: this.#playerId, itemId: row.dataset.id });
  }

  /**
   * Teclat (amb la llista activa): Supr, Esc, Ctrl+A, ↑/↓ (Maj: ampliar la selecció)
   * i les dreceres C (cue), D (duplicar), B (bucle), A (avanç automàtic). D-033.
   * @param {KeyboardEvent} e
   */
  #onKey(e) {
    const key = e.key.toLowerCase();
    const plain = !e.ctrlKey && !e.altKey && !e.metaKey;

    if (e.key === 'Delete' && this.#selected.size > 0) {
      this.#send('playlist.remove', { player: this.#playerId, ids: [...this.#selected] });
      this.#selected.clear();
    } else if (e.key === 'Escape') {
      this.#selected.clear();
      this.update(null, null);
    } else if (key === 'a' && e.ctrlKey) {
      this.#selected = new Set(this.#ids());
      this.update(null, null);
    } else if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      this.#moveSelection(e.key === 'ArrowUp' ? -1 : +1, e.shiftKey);
    } else if (plain && key === 'c') {
      this.#cueSelected();
    } else if (plain && key === 'd') {
      this.#duplicateSelected();
    } else if (plain && key === 'b') {
      this.#toggleSelected('loop');
    } else if (plain && key === 'a') {
      this.#toggleSelected('autoNext');
    } else {
      return;
    }
    e.preventDefault();
  }

  /** C: la (primera) pista seleccionada passa a ser la del cue; si ja ho era, es treu. */
  #cueSelected() {
    const item = this.#selectedItems()[0];
    if (item) this.#toggleFlag(item.id, 'cue');
  }

  /** D: duplica les seleccionades, cada una just a sota. */
  #duplicateSelected() {
    const ids = this.#selectedItems().map((/** @type {any} */ i) => i.id);
    if (ids.length > 0) this.#send('playlist.duplicate', { player: this.#playerId, ids });
  }

  /**
   * B i A: canvia la marca de totes les seleccionades (segons com estigui la primera).
   * L'avanç automàtic no s'aplica a les pistes amb bucle (D-034).
   */
  async #toggleSelected(/** @type {'loop' | 'autoNext'} */ flag) {
    let items = this.#selectedItems();
    if (flag === 'autoNext') items = items.filter((/** @type {any} */ i) => !i.loop);
    if (items.length === 0) {
      if (flag === 'autoNext') this.#onMessage('Amb el bucle activat no es pot activar l\'avanç automàtic.');
      return;
    }
    const value = !items[0][flag];
    for (const item of items)
      await this.#send('playlist.setFlags', { player: this.#playerId, itemId: item.id, [flag]: value });
  }

  /** @param {MouseEvent} e */
  #onContextMenu(e) {
    const row = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('.pl-row'));
    if (!row) return;
    const item = this.#playlist.items.find((/** @type {any} */ i) => i.id === row.dataset.id);
    if (!item) return;

    if (!this.#selected.has(item.id)) {
      this.#selected = new Set([item.id]);
      this.#anchor = item.id;
      this.#lead = item.id;
      this.update(null, null);
    }
    const selected = this.#selectedItems();

    showContextMenu(e, [
      {
        label: selected.length > 1 ? `Informació (${selected.length} pistes)` : 'Informació',
        icon: 'bi-info-circle',
        action: () => (selected.length > 1 ? this.#showSelectionInfo(selected) : this.#showInfo(item)),
      },
      { label: 'Duplicar (D)', icon: 'bi-copy', action: () => this.#duplicateSelected() },
      { label: 'Mostrar a l\'Explorador', icon: 'bi-folder2-open', action: () => bridge?.showInFolder(item.path) },
    ]);
  }

  /** Diverses pistes seleccionades: quantes són i la durada total. */
  #showSelectionInfo(/** @type {any[]} */ items) {
    const total = items.reduce((sum, i) => sum + (i.duration ?? 0), 0);
    const unknown = items.filter((i) => i.duration == null).length;
    const problems = items.filter((i) => i.status === 'error' || i.status === 'missing').length;
    showInfo(`${items.length} pistes seleccionades`, [
      ['Durada total', formatDuration(total) + (unknown > 0 ? ` (sense comptar-ne ${unknown} de durada desconeguda)` : '')],
      ['Amb problemes', problems > 0 ? problems : null],
      ['Primera', items[0].name],
      ['Última', items[items.length - 1].name],
    ]);
  }

  /** Finestra amb les metadades (ordre track.info). */
  async #showInfo(/** @type {any} */ item) {
    try {
      const { info } = await this.#request('track.info', { path: item.path });
      showInfo(item.name, [
        ['Títol', info.title],
        ['Artista', info.artist],
        ['Àlbum', info.album],
        ['Any', info.year],
        ['Gènere', info.genre],
        ['Comentari', info.comment],
        ['Durada', formatDuration(info.duration)],
        ['Format', info.format],
        ['Mida', `${(info.fileSize / 1_048_576).toLocaleString('ca-ES', { maximumFractionDigits: 1 })} MB`],
        ['Fitxer', info.path],
      ]);
    } catch (err) {
      showInfo(item.name, [
        ['Problema', /** @type {Error} */ (err).message],
        ['Fitxer', item.path],
      ]);
    }
  }

  #toggleFlag(/** @type {string} */ id, /** @type {string} */ flag) {
    const item = this.#playlist.items.find((/** @type {any} */ i) => i.id === id);
    if (!item) return;
    const params = { player: this.#playerId, itemId: id };
    if (flag === 'loop') Object.assign(params, { loop: !item.loop });
    else if (flag === 'auto') Object.assign(params, { autoNext: !item.autoNext });
    else Object.assign(params, { cue: this.#playlist.cueId !== id });
    this.#send('playlist.setFlags', params);
  }

  // -------------------------------------------------------------------------
  // Arrossegar
  // -------------------------------------------------------------------------

  #setupDrag() {
    const rows = this.#rows;

    rows.addEventListener('dragstart', (e) => {
      const row = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('.pl-row'));
      if (!row || !e.dataTransfer) return;
      const id = /** @type {string} */ (row.dataset.id);
      if (!this.#selected.has(id)) {
        this.#selected = new Set([id]);
        this.#anchor = id;
      }
      // En l'ordre de la llista; les rutes, per si es deixen anar en una altra llista.
      const items = this.#playlist.items.filter((/** @type {any} */ i) => this.#selected.has(i.id));
      e.dataTransfer.setData(DRAG_TYPE, JSON.stringify({
        player: this.#playerId,
        ids: items.map((/** @type {any} */ i) => i.id),
        paths: items.map((/** @type {any} */ i) => i.path),
      }));
      e.dataTransfer.effectAllowed = 'copyMove';
      this.#dragging = true;
      this.#applySelection();
    });

    rows.addEventListener('dragover', (e) => {
      if (!e.dataTransfer) return;
      const types = e.dataTransfer.types;
      if (!types.includes(DRAG_TYPE) && !types.includes('Files')) return;
      e.preventDefault();
      e.stopPropagation(); // el panell no ha de mostrar el seu avís d'arrossegar
      // El cursor diu si es mourà o es copiarà (amb Ctrl).
      if (types.includes(DRAG_TYPE)) e.dataTransfer.dropEffect = e.ctrlKey ? 'copy' : 'move';
      this.#showDropMark(this.#dropPosition(e));
    });

    rows.addEventListener('dragleave', (e) => {
      if (!rows.contains(/** @type {Node | null} */ (e.relatedTarget))) this.#showDropMark(null);
    });

    rows.addEventListener('drop', (e) => {
      if (!e.dataTransfer) return;
      e.preventDefault();
      e.stopPropagation();
      const beforeId = this.#dropPosition(e).beforeId;
      this.#endDrag();

      // Pistes d'una llista (aquesta o una altra): es mouen; amb Ctrl, es copien (D-033).
      const internal = e.dataTransfer.getData(DRAG_TYPE);
      if (internal) {
        const data = JSON.parse(internal);
        this.#send('playlist.transfer', {
          player: data.player, ids: data.ids, toPlayer: this.#playerId, beforeId, copy: e.ctrlKey,
        });
        return;
      }

      const paths = [...e.dataTransfer.files].map((f) => bridge?.pathForFile(f)).filter(Boolean);
      if (paths.length > 0) this.#send('playlist.add', { player: this.#playerId, paths, beforeId });
    });

    rows.addEventListener('dragend', () => this.#endDrag());
  }

  /**
   * On aniria el que s'arrossega: davant de quina pista (o al final, si beforeId és null).
   * @param {DragEvent} e
   * @returns {{ beforeId: string | null, row: HTMLElement | null, after: boolean }}
   */
  #dropPosition(e) {
    const row = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest?.('.pl-row'));
    if (!row) return { beforeId: null, row: null, after: true };
    const rect = row.getBoundingClientRect();
    const after = e.clientY > rect.top + rect.height / 2;
    const target = after ? /** @type {HTMLElement | null} */ (row.nextElementSibling) : row;
    return { beforeId: target?.dataset.id ?? null, row, after };
  }

  /** Línia que indica on es deixarà anar. */
  #showDropMark(/** @type {{ row: HTMLElement | null, after: boolean } | null} */ position) {
    for (const marked of this.#rows.querySelectorAll('.drop-before, .drop-after'))
      marked.classList.remove('drop-before', 'drop-after');
    this.#rows.classList.toggle('drop-end', !!position && !position.row);
    if (position?.row) position.row.classList.add(position.after ? 'drop-after' : 'drop-before');
  }

  // -------------------------------------------------------------------------
  // Llistes desades (D-035)
  // -------------------------------------------------------------------------

  /** Obrir una llista: si l'actual no és buida, es pregunta si se substitueix o s'hi afegeix. */
  async #openList() {
    const path = await bridge?.openPlaylistFile();
    if (!path) return;

    let replace = true;
    if (this.#playlist.items.length > 0) {
      const choice = await choose({
        title: 'Obrir una llista',
        icon: 'bi-folder2-open',
        message: `La llista del Reproductor ${this.#playerId} no és buida.\n\nQuè vols fer amb la llista nova?` +
          (this.#player.state === 'playing' ? '\n\nLa pista que sona continuarà sonant.' : ''),
        buttons: [
          { label: 'Cancel·lar', value: 'cancel' },
          { label: 'Afegir-la al final', value: 'append', style: 'secondary', icon: 'bi-plus-lg' },
          { label: 'Substituir l\'actual', value: 'replace', style: 'primary', icon: 'bi-arrow-repeat' },
        ],
      });
      if (choice === 'cancel') return;
      replace = choice === 'replace';
    }

    try {
      const result = await this.#request('playlist.load', { player: this.#playerId, path, replace });
      if (result.missing > 0)
        this.#onMessage(`${result.missing} ${result.missing === 1 ? 'pista no s\'ha trobat' : 'pistes no s\'han trobat'} (marcades amb ⚠).`);
    } catch (err) {
      this.#onMessage(/** @type {Error} */ (err).message);
    }
  }

  async #saveList() {
    if (this.#playlist.items.length === 0) {
      this.#onMessage('La llista és buida: no hi ha res per desar.');
      return;
    }
    const path = await bridge?.savePlaylistFile(`Reproductor ${this.#playerId}`);
    if (!path) return;
    try {
      const result = await this.#request('playlist.save', { player: this.#playerId, path });
      this.#onMessage(`Llista desada: ${result.path.split(/[\\/]/).pop()} (${result.count} pistes).`);
    } catch (err) {
      this.#onMessage(/** @type {Error} */ (err).message);
    }
  }

  /** Netejar la llista: treu totes les pistes, amb confirmació. La pista que sona continua sonant. */
  async #clearList() {
    const ids = this.#playlist.items.map((i) => i.id);
    if (ids.length === 0) return;

    const choice = await choose({
      title: 'Netejar la llista',
      icon: 'bi-trash3',
      message: `Vols treure les ${ids.length} pistes de la llista del Reproductor ${this.#playerId}?\n\nEls fitxers no s'esborren del disc.` +
        (this.#player.state === 'playing' ? '\n\nLa pista que sona continuarà sonant fins al final.' : ''),
      buttons: [
        { label: 'Cancel·lar', value: 'cancel' },
        { label: 'Netejar', value: 'clear', style: 'danger', icon: 'bi-trash3' },
      ],
    });
    if (choice !== 'clear') return;
    this.#send('playlist.remove', { player: this.#playerId, ids });
  }

  /** Afegeix fitxers al final (botó «Afegir» o fitxers deixats anar fora de la llista). */
  addFiles(/** @type {string[]} */ paths) {
    if (paths.length > 0) this.#send('playlist.add', { player: this.#playerId, paths });
  }

  async #send(/** @type {string} */ cmd, /** @type {object} */ params) {
    try {
      await this.#request(cmd, params);
    } catch (err) {
      this.#onMessage(/** @type {Error} */ (err).message);
    }
  }
}

/**
 * Botó de marca d'una pista (només icona; el títol diu què fa).
 * @param {string} name
 * @param {boolean} on
 * @param {string} icon
 * @param {string} title
 * @param {string | null} [disabledReason]  si no es pot fer servir, per què (es mostra en passar el ratolí)
 */
function flag(name, on, icon, title, disabledReason = null) {
  return `<button class="pl-flag pl-flag-${name}${on ? ' on' : ''}" type="button" data-flag="${name}"
    title="${disabledReason ?? title}" aria-pressed="${on}" draggable="false"${disabledReason ? ' disabled' : ''}>
    <i class="bi ${icon}"></i></button>`;
}
