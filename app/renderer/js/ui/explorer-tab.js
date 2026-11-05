// @ts-check
// Pestanya de l'explorador del panell lateral (fase 4, pas 2c; D-064): Àudios (i, al pas 2d, Llistes).
//
// - A dalt: la carpeta (les del perfil, configurables amb ⚙), el camí dins de la carpeta i un filtre.
// - Clic a una subcarpeta: s'hi entra. Clic a un fitxer: se selecciona (Ctrl i Maj, com a les llistes).
// - Els fitxers s'arrosseguen a un reproductor (on es deixen anar).
// - Doble clic a un àudio: s'escolta a la preescolta. Botó dret: més opcions.
// El contingut de les carpetes el llegeix el motor (explorer.list): aquí només es mostra.

import { escapeHtml } from './format.js';
import { showContextMenu } from './context-menu.js';
import { togglePreview, previewButton } from './cue-actions.js';
import { PATHS_DRAG_TYPE } from './drag-types.js';
import { editExplorerFolders } from './explorer-folders-dialog.js';
import { choose } from './dialogs.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */
/** @typedef {{ name: string, path: string, exists: boolean }} RootFolder */
/** @typedef {{ name: string, path: string, size?: number }} Entry */

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

const KINDS = {
  audios: { title: 'Àudios', icon: 'bi-folder2-open', fileIcon: 'bi-music-note-beamed', what: 'àudios' },
  lists: { title: 'Llistes', icon: 'bi-collection-play', fileIcon: 'bi-collection-play', what: 'llistes' },
};

export class ExplorerTab {
  /** @type {HTMLElement} */ element;
  #kind;
  #store;
  #request;
  /** @type {Record<string, HTMLElement>} */ #el = {};
  /** Carpetes del perfil per a aquesta pestanya. @type {RootFolder[]} */ #roots = [];
  /** Carpeta del perfil on som (índex a #roots). */ #rootIndex = 0;
  /** Carpeta que es mostra (la de dalt o una subcarpeta). @type {string | null} */ #path = null;
  /** @type {{ folders: Entry[], files: Entry[], truncated: boolean } | null} */ #listing = null;
  /** Error en llegir la carpeta (es mostra al lloc de la llista). @type {string | null} */ #error = null;
  /** Fitxers seleccionats (rutes). @type {Set<string>} */ #selected = new Set();
  /** @type {string | null} */ #anchor = null;

  /**
   * @param {'audios' | 'lists'} kind
   * @param {Store} store
   * @param {Request} request
   */
  constructor(kind, store, request) {
    this.#kind = kind;
    this.#store = store;
    this.#request = request;
    const k = KINDS[kind];

    this.element = document.createElement('section');
    this.element.className = 'card panel explorer';
    this.element.setAttribute('aria-label', k.title);
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-title"><i class="bi ${k.icon}"></i> ${k.title}</span>
        <button class="btn btn-sm btn-outline-secondary ex-tool js-manage" type="button" title="Carpetes d'aquesta pestanya…">
          <i class="bi bi-gear"></i>
        </button>
      </div>
      <div class="ex-nav">
        <select class="form-select form-select-sm js-root" aria-label="Carpeta"></select>
        <button class="btn btn-sm btn-outline-secondary ex-tool js-up" type="button" title="Carpeta de dalt"><i class="bi bi-arrow-up"></i></button>
        <button class="btn btn-sm btn-outline-secondary ex-tool js-refresh" type="button" title="Tornar a llegir la carpeta"><i class="bi bi-arrow-clockwise"></i></button>
      </div>
      <div class="ex-crumbs js-crumbs"></div>
      <input class="form-control form-control-sm ex-filter js-filter" type="search" placeholder="Filtrar per nom…" aria-label="Filtrar per nom">
      <div class="ex-rows js-rows" tabindex="0" aria-label="Contingut de la carpeta"></div>
      <div class="ex-status js-status"></div>`;

    for (const name of ['manage', 'root', 'up', 'refresh', 'crumbs', 'filter', 'rows', 'status']) {
      this.#el[name] = /** @type {HTMLElement} */ (this.element.querySelector(`.js-${name}`));
    }

    this.#el.manage.addEventListener('click', () => this.#manageFolders());
    this.#el.root.addEventListener('change', () => {
      this.#rootIndex = Number(/** @type {HTMLSelectElement} */ (this.#el.root).value);
      this.#open(this.#roots[this.#rootIndex]?.path ?? null);
    });
    this.#el.up.addEventListener('click', () => this.#goUp());
    this.#el.refresh.addEventListener('click', () => this.#open(this.#path));
    this.#el.filter.addEventListener('input', () => this.#renderRows());
    this.#el.filter.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') { /** @type {HTMLInputElement} */ (this.#el.filter).value = ''; this.#renderRows(); }
    });
    this.#el.crumbs.addEventListener('click', (e) => {
      const crumb = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('[data-path]'));
      if (crumb) this.#open(/** @type {string} */ (crumb.dataset.path));
    });
    this.#setupRows();

    // La 🎧 de la fila que sona a la preescolta s'encén.
    store.addEventListener('cue', () => this.#updatePreview());

    this.load();
  }

  /** Llegeix les carpetes del perfil i obre l'última que es feia servir (o la primera). */
  async load() {
    try {
      const result = await this.#request('explorer.get');
      this.#roots = result[this.#kind];
    } catch (err) {
      this.#roots = [];
      this.#error = /** @type {Error} */ (err).message;
    }
    const remembered = localStorage.getItem(this.#storageKey()) ?? '';
    const index = this.#roots.findIndex((r) => isInside(remembered, r.path));
    this.#rootIndex = Math.max(0, index);
    this.#renderRoots();
    await this.#open(index >= 0 ? remembered : this.#roots[0]?.path ?? null);
  }

  #storageKey() {
    return `obp.explorer.${this.#kind}`;
  }

  // -------------------------------------------------------------------------
  // Navegar
  // -------------------------------------------------------------------------

  /** Mostra el contingut d'una carpeta (null = cap carpeta configurada). @param {string | null} path */
  async #open(path) {
    this.#path = path;
    this.#selected.clear();
    this.#anchor = null;
    this.#listing = null;
    this.#error = null;
    if (path) localStorage.setItem(this.#storageKey(), path);
    this.#renderCrumbs();
    this.#renderRows(path ? 'Llegint la carpeta…' : undefined);
    if (!path) return;

    try {
      const listing = await this.#request('explorer.list', { kind: this.#kind, path });
      if (this.#path !== path) return; // mentrestant s'ha anat a una altra carpeta
      this.#listing = listing;
    } catch (err) {
      if (this.#path !== path) return;
      this.#error = /** @type {Error} */ (err).message;
    }
    this.#renderRows();
  }

  #goUp() {
    const root = this.#roots[this.#rootIndex];
    if (!root || !this.#path || samePath(this.#path, root.path)) return;
    this.#open(parentOf(this.#path));
  }

  async #manageFolders() {
    const roots = await editExplorerFolders(this.#kind, KINDS[this.#kind].title, this.#roots, this.#request);
    if (!roots) return;
    this.#roots = roots[this.#kind];
    // Es queda a la mateixa carpeta si encara hi és; si no, a la primera.
    const index = this.#roots.findIndex((r) => this.#path && isInside(this.#path, r.path));
    this.#rootIndex = Math.max(0, index);
    this.#renderRoots();
    this.#open(index >= 0 ? this.#path : this.#roots[0]?.path ?? null);
  }

  /** Pestanya Llistes buida: afegeix la carpeta on es desen les llistes per defecte. */
  async #addDefaultListsFolder() {
    try {
      const path = await bridge?.defaultPlaylistsFolder();
      if (!path) return;
      const result = await this.#request('explorer.setFolders', {
        kind: this.#kind,
        folders: [...this.#roots.map((r) => ({ name: r.name, path: r.path })), { name: 'Llistes', path }],
      });
      this.#roots = result[this.#kind];
      this.#rootIndex = this.#roots.length - 1;
      this.#renderRoots();
      this.#open(this.#roots[this.#rootIndex].path);
    } catch (err) {
      this.#el.status.textContent = /** @type {Error} */ (err).message;
    }
  }

  // -------------------------------------------------------------------------
  // Pintar
  // -------------------------------------------------------------------------

  #renderRoots() {
    const select = /** @type {HTMLSelectElement} */ (this.#el.root);
    select.innerHTML = this.#roots.length === 0
      ? '<option value="">— Cap carpeta —</option>'
      : this.#roots.map((r, i) =>
        `<option value="${i}">${escapeHtml(r.name)}${r.exists ? '' : ' (no hi és)'}</option>`).join('');
    select.value = String(this.#rootIndex);
    select.disabled = this.#roots.length === 0;
  }

  /** Camí dins de la carpeta del perfil: «Sintonies › Dilluns › Matí». Cada tros porta a aquella carpeta. */
  #renderCrumbs() {
    const root = this.#roots[this.#rootIndex];
    /** @type {HTMLButtonElement} */ (this.#el.up).disabled = !root || !this.#path || samePath(this.#path, root.path);
    if (!root || !this.#path) {
      this.#el.crumbs.innerHTML = '';
      return;
    }
    // Si per algun motiu la ruta no comença com la de la carpeta del perfil, només el nom de la carpeta.
    const rest = isInside(this.#path, root.path)
      ? this.#path.slice(trimSlash(root.path).length).split(/[\\/]/).filter(Boolean)
      : [trimSlash(this.#path).split(/[\\/]/).pop() ?? ''];
    let current = trimSlash(root.path);
    const parts = [`<button type="button" class="ex-crumb" data-path="${escapeHtml(current)}">${escapeHtml(root.name)}</button>`];
    for (const part of rest) {
      current = `${current}\\${part}`;
      parts.push(`<button type="button" class="ex-crumb" data-path="${escapeHtml(current)}">${escapeHtml(part)}</button>`);
    }
    this.#el.crumbs.innerHTML = parts.join('<i class="bi bi-chevron-right ex-crumb-sep"></i>');
  }

  /** @param {string} [message] text en lloc de la llista (p. ex. mentre es llegeix) */
  #renderRows(message) {
    const rows = this.#el.rows;
    const k = KINDS[this.#kind];

    if (this.#roots.length === 0) {
      rows.innerHTML = `
        <div class="ex-empty">
          <p>Encara no hi ha cap carpeta de ${k.what} en aquest perfil.</p>
          ${this.#kind === 'lists' ? `<button type="button" class="btn btn-sm btn-primary js-add-default">
            <i class="bi bi-plus-lg"></i> Afegir la carpeta «Llistes»</button>
            <p class="ex-hint">És on es desen les llistes si no se'n tria una altra.</p>` : ''}
          <button type="button" class="btn btn-sm ${this.#kind === 'lists' ? 'btn-outline-primary' : 'btn-primary'} js-add-first">
            <i class="bi bi-plus-lg"></i> Afegir una carpeta…</button>
        </div>`;
      rows.querySelector('.js-add-first')?.addEventListener('click', () => this.#manageFolders());
      rows.querySelector('.js-add-default')?.addEventListener('click', () => this.#addDefaultListsFolder());
      this.#el.status.textContent = '';
      return;
    }
    if (this.#error) {
      rows.innerHTML = `<div class="ex-empty ex-error"><i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(this.#error)}</div>`;
      this.#el.status.textContent = '';
      return;
    }
    if (!this.#listing) {
      rows.innerHTML = `<div class="ex-empty">${escapeHtml(message ?? '')}</div>`;
      this.#el.status.textContent = '';
      return;
    }

    const filter = normalize(/** @type {HTMLInputElement} */ (this.#el.filter).value.trim());
    const matches = (/** @type {Entry} */ e) => !filter || normalize(e.name).includes(filter);
    const folders = this.#listing.folders.filter(matches);
    const files = this.#listing.files.filter(matches);

    if (folders.length === 0 && files.length === 0) {
      rows.innerHTML = `<div class="ex-empty">${filter ? 'Cap nom no coincideix amb el filtre.' : `Aquesta carpeta no té ${k.what} ni subcarpetes.`}</div>`;
    } else {
      rows.innerHTML = [
        ...folders.map((f) => `
          <div class="ex-row ex-folder" data-folder="${escapeHtml(f.path)}" title="${escapeHtml(f.name)}">
            <i class="bi bi-folder-fill"></i><span class="ex-name">${escapeHtml(f.name)}</span>
          </div>`),
        ...files.map((f) => `
          <div class="ex-row ex-file${this.#selected.has(f.path) ? ' ex-selected' : ''}" draggable="true"
               data-path="${escapeHtml(f.path)}" title="${escapeHtml(f.name)}">
            <i class="bi ${k.fileIcon}"></i><span class="ex-name">${escapeHtml(f.name)}</span>
            ${this.#kind === 'audios' ? previewButton(this.#store.isPreviewing(f.path), 'data-action="preview"', 'ex-btn') : ''}
          </div>`),
      ].join('');
      this.#updatePreview();
    }

    const counts = [`${files.length} ${k.what}`, `${folders.length} carpetes`];
    if (filter) counts.push(`filtre: «${/** @type {HTMLInputElement} */ (this.#el.filter).value.trim()}»`);
    if (this.#listing.truncated) counts.push('n\'hi ha més: no es mostren tots');
    this.#el.status.textContent = counts.join(' · ');
  }

  /** Encén la fila (i la 🎧) del fitxer que sona a la preescolta. */
  #updatePreview() {
    for (const row of this.#el.rows.querySelectorAll('.ex-file')) {
      const on = this.#store.isPreviewing(/** @type {HTMLElement} */ (row).dataset.path);
      row.classList.toggle('ex-previewing', on);
      const button = row.querySelector('.preview-btn');
      if (button) {
        button.classList.toggle('on', on);
        button.setAttribute('aria-pressed', String(on));
        button.setAttribute('title', on ? 'Aturar la preescolta' : 'Preescoltar (pels auriculars)');
      }
    }
  }

  // -------------------------------------------------------------------------
  // Files: clic, doble clic, botó dret i arrossegar
  // -------------------------------------------------------------------------

  #setupRows() {
    const rows = this.#el.rows;

    rows.addEventListener('click', (e) => {
      const target = /** @type {HTMLElement} */ (e.target);
      const folder = /** @type {HTMLElement | null} */ (target.closest('.ex-folder'));
      if (folder) {
        this.#open(/** @type {string} */ (folder.dataset.folder));
        return;
      }
      const row = /** @type {HTMLElement | null} */ (target.closest('.ex-file'));
      if (!row) return;
      const path = /** @type {string} */ (row.dataset.path);
      if (target.closest('[data-action="preview"]')) {
        this.#preview(path);
        return;
      }
      this.#select(path, e);
    });

    rows.addEventListener('dblclick', (e) => {
      const target = /** @type {HTMLElement} */ (e.target);
      const row = /** @type {HTMLElement | null} */ (target.closest('.ex-file'));
      if (!row || target.closest('[data-action]')) return;
      const path = /** @type {string} */ (row.dataset.path);
      // Àudio: s'escolta a la preescolta. Llista: se'n veu el contingut.
      if (this.#kind === 'audios') this.#preview(path);
      else this.#showListContents(path);
    });

    rows.addEventListener('contextmenu', (e) => {
      const row = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('.ex-file'));
      if (!row) return;
      const path = /** @type {string} */ (row.dataset.path);
      if (!this.#selected.has(path)) this.#select(path, null);
      this.#showMenu(e);
    });

    rows.addEventListener('dragstart', (e) => {
      const row = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('.ex-file'));
      if (!row || !e.dataTransfer) return;
      const path = /** @type {string} */ (row.dataset.path);
      if (!this.#selected.has(path)) this.#select(path, null);
      e.dataTransfer.setData(PATHS_DRAG_TYPE, JSON.stringify(this.#selectedPaths()));
      e.dataTransfer.effectAllowed = 'copy';
    });
  }

  /**
   * Selecció com a les llistes: clic = només aquest; Ctrl = afegir o treure; Maj = de l'últim fins aquí.
   * @param {string} path
   * @param {MouseEvent | null} e
   */
  #select(path, e) {
    const visible = [...this.#el.rows.querySelectorAll('.ex-file')].map((r) => /** @type {string} */ (/** @type {HTMLElement} */ (r).dataset.path));
    if (e?.shiftKey && this.#anchor && visible.includes(this.#anchor)) {
      const [a, b] = [visible.indexOf(this.#anchor), visible.indexOf(path)].sort((x, y) => x - y);
      if (!e.ctrlKey) this.#selected.clear();
      for (const p of visible.slice(a, b + 1)) this.#selected.add(p);
    } else if (e?.ctrlKey) {
      if (this.#selected.has(path)) this.#selected.delete(path); else this.#selected.add(path);
      this.#anchor = path;
    } else {
      this.#selected = new Set([path]);
      this.#anchor = path;
    }
    for (const row of this.#el.rows.querySelectorAll('.ex-file')) {
      row.classList.toggle('ex-selected', this.#selected.has(/** @type {string} */ (/** @type {HTMLElement} */ (row).dataset.path)));
    }
  }

  /** Les rutes seleccionades, en l'ordre en què es veuen. */
  #selectedPaths() {
    return [...this.#el.rows.querySelectorAll('.ex-file')]
      .map((r) => /** @type {string} */ (/** @type {HTMLElement} */ (r).dataset.path))
      .filter((p) => this.#selected.has(p));
  }

  /** @param {string} path */
  async #preview(path) {
    const problem = await togglePreview(this.#store, this.#request, path);
    if (problem) this.#el.status.textContent = problem;
  }

  /** @param {MouseEvent} e */
  #showMenu(e) {
    const paths = this.#selectedPaths();
    const single = paths.length === 1 ? paths[0] : null;
    const players = [...this.#store.players.keys()];
    const what = paths.length === 1 ? '' : ` (${paths.length})`;

    /** @type {import('./context-menu.js').MenuItem[]} */
    const items = [];
    if (single && this.#kind === 'audios') {
      items.push({
        label: this.#store.isPreviewing(single) ? 'Aturar la preescolta' : 'Escoltar a la preescolta',
        icon: 'bi-headphones',
        action: () => this.#preview(single),
      });
      items.push({ separator: true });
    }
    if (single && this.#kind === 'lists') {
      items.push({ label: 'Veure el contingut', icon: 'bi-eye', action: () => this.#showListContents(single) });
      items.push({ separator: true });
    }
    for (const id of players) {
      items.push({
        label: `Afegir al reproductor ${id}${what}`,
        icon: 'bi-plus-lg',
        action: () => this.#addTo(id, paths),
      });
    }
    // Una llista també pot substituir la d'un reproductor (p. ex. la secció del dia de «La Central»).
    if (single && this.#kind === 'lists') {
      items.push({ separator: true });
      for (const id of players) {
        items.push({
          label: `Substituir la llista del reproductor ${id}…`,
          icon: 'bi-arrow-left-right',
          action: () => this.#replaceList(id, single),
        });
      }
    }
    if (single) {
      items.push({ separator: true });
      if (this.#kind === 'audios' && /\.mp3$/i.test(single)) {
        items.push({ label: 'Obrir a l\'editor', icon: 'bi-scissors', action: () => bridge?.openEditor(single) });
      }
      items.push({ label: 'Mostrar a la carpeta', icon: 'bi-folder2-open', action: () => bridge?.showInFolder(single) });
    }
    showContextMenu(e, items);
  }

  /** Substitueix la llista d'un reproductor per una llista desada (amb confirmació). @param {string} playerId @param {string} path */
  async #replaceList(playerId, path) {
    const name = fileNameOf(path);
    const current = this.#store.playlists.get(playerId)?.items?.length ?? 0;
    const playing = this.#store.players.get(playerId)?.state === 'playing';
    const choice = await choose({
      title: `Substituir la llista del reproductor ${playerId}`,
      icon: 'bi-arrow-left-right',
      message: `La llista del reproductor ${playerId}${current ? ` (${current} elements)` : ''} es canviarà per «${name}».`
        + (playing ? '\nLa pista que sona continua sonant fins que s\'acabi o l\'aturis.' : ''),
      buttons: [
        { label: 'Cancel·lar', value: 'cancel' },
        { label: 'Substituir', value: 'replace', style: 'primary' },
      ],
    });
    if (choice !== 'replace') return;
    try {
      const result = await this.#request('playlist.load', { player: playerId, path, replace: true });
      this.#el.status.textContent = `Reproductor ${playerId}: «${name}»`
        + (result.missing ? ` (${result.missing} fitxers no hi són)` : '');
    } catch (err) {
      this.#el.status.textContent = /** @type {Error} */ (err).message;
    }
  }

  /** Finestra amb el contingut d'una llista desada (sense carregar-la). @param {string} path */
  async #showListContents(path) {
    let result;
    try {
      result = await this.#request('playlist.peek', { path });
    } catch (err) {
      this.#el.status.textContent = /** @type {Error} */ (err).message;
      return;
    }
    showListContents(fileNameOf(path), result);
  }

  /** @param {string} playerId @param {string[]} paths */
  async #addTo(playerId, paths) {
    try {
      await this.#request('playlist.add', { player: playerId, paths });
      this.#el.status.textContent = `Afegit al reproductor ${playerId}: ${paths.length === 1 ? 'una pista' : `${paths.length} fitxers`}.`;
    } catch (err) {
      this.#el.status.textContent = /** @type {Error} */ (err).message;
    }
  }
}

/**
 * Finestra amb el contingut d'una llista: les seccions i les pistes, i les que no hi són en vermell.
 * @param {string} name
 * @param {{ items: any[], tracks: number, missing: number }} contents
 */
function showListContents(name, contents) {
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog ex-contents-dialog';
  const summary = `${contents.tracks} pistes` + (contents.missing ? ` · ${contents.missing} no hi són` : '');
  const rows = contents.items.map((item) => item.section !== undefined
    ? `<div class="ex-contents-section"><i class="bi bi-chevron-down"></i> ${escapeHtml(item.section)}</div>`
    : `<div class="ex-contents-track${item.exists ? '' : ' missing'}" title="${escapeHtml(item.path)}">
         <i class="bi ${item.exists ? 'bi-music-note-beamed' : 'bi-exclamation-triangle-fill'}"></i>
         <span class="ex-name">${escapeHtml(fileNameOf(item.path))}</span>
         ${item.exists ? '' : '<span class="ex-missing-label">no hi és</span>'}
       </div>`).join('');
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-collection-play"></i> ${escapeHtml(name)}</h2>
      <div class="ex-contents-summary">${escapeHtml(summary)}</div>
      <div class="ex-contents-list">${rows || '<div class="ex-folders-empty">La llista és buida.</div>'}</div>
      <div class="obp-dialog-buttons">
        <button class="btn btn-primary" value="ok">Tancar</button>
      </div>
    </form>`;
  document.body.appendChild(dialog);
  dialog.showModal();
  dialog.addEventListener('close', () => dialog.remove());
}

/** Nom del fitxer sense la carpeta. @param {string} path */
function fileNameOf(path) {
  return trimSlash(path).split(/[\\/]/).pop() ?? path;
}

// ---------------------------------------------------------------------------
// Rutes (Windows: no es distingeixen majúscules)
// ---------------------------------------------------------------------------

/** @param {string} path */
function trimSlash(path) {
  return path.replace(/[\\/]+$/, '');
}

/** @param {string} a @param {string} b */
function samePath(a, b) {
  return trimSlash(a).toLowerCase() === trimSlash(b).toLowerCase();
}

/** `path` és `root` o és a dins. @param {string} path @param {string} root */
function isInside(path, root) {
  const p = `${trimSlash(path).toLowerCase()}\\`;
  return p.startsWith(`${trimSlash(root).toLowerCase()}\\`);
}

/** @param {string} path */
function parentOf(path) {
  const trimmed = trimSlash(path);
  return trimmed.slice(0, Math.max(trimmed.lastIndexOf('\\'), trimmed.lastIndexOf('/')));
}

/** Per filtrar: sense majúscules ni accents («Cançó» = «canco»). @param {string} text */
function normalize(text) {
  return text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}
