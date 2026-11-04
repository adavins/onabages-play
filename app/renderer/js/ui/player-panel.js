// @ts-check
// Panell d'un reproductor: pista actual, controls, temps, sortida, volum i llista.
// La interfície només envia ordres i mostra l'estat que rep del motor.

import { PLAYER_STATES, stateBadge } from './states.js';
import { deviceWarning } from './device-line.js';
import { DeviceSelect } from './device-select.js';
import { LevelMeter } from './meter.js';
import { VolumeFader } from './fader.js';
import { PlaylistView, sectionOf } from './playlist-view.js';
import { escapeHtml, formatDuration } from './format.js';
import { playKey } from './shortcuts.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** Segons abans del final en què el temps restant canvia d'aspecte, si encara no s'han llegit els ajustos (especificació 6.2). */
const DEFAULT_END_WARNING_SECONDS = 10;

/** Estats en què es pot saltar dins la pista (player.seek). */
const CAN_SEEK = ['ready', 'playing', 'paused'];

/** Quant temps es mostra un missatge d'error d'una ordre. */
const MESSAGE_MS = 5000;

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

export class PlayerPanel {
  /** @type {HTMLElement} */ element;
  #store;
  #id;
  #request;
  #deviceSelect;
  #meter;
  #fader;
  #playlist;
  /** @type {string | null} */ #lastItemId = null;
  /** @type {Record<string, HTMLElement>} */ #el = {};
  /** @type {number | undefined} */ #messageTimer;
  /** Mentre s'arrossega la barra de posició: on es deixaria (0..1). Si no, null. */
  /** @type {number | null} */ #dragFraction = null;
  /** El comptador gran mostra el temps transcorregut en lloc del restant. */
  #showElapsed = false;

  /**
   * @param {Store} store
   * @param {string} id  identificador del reproductor ("1", "2"...)
   * @param {Request} request  envia una ordre al motor
   */
  constructor(store, id, request) {
    this.#store = store;
    this.#id = id;
    this.#request = request;

    this.element = document.createElement('section');
    this.element.className = 'card panel player';
    this.element.setAttribute('aria-label', `Reproductor ${id}`);
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-title"><i class="bi bi-music-note-list"></i> Reproductor ${escapeHtml(id)}</span>
        <span class="js-state"></span>
      </div>
      <div class="card-body panel-body">
        <div class="js-device-slot"></div>
        <div class="js-device-warning"></div>

        <div class="track-row">
          <div class="track js-track"></div>
          <button class="btn btn-sm btn-outline-secondary js-edit" type="button" title="Obrir a l'editor la pista actual (o la seleccionada a la llista)">
            <i class="bi bi-scissors"></i>
          </button>
          <button class="btn btn-sm btn-outline-secondary js-open" type="button" title="Afegir pistes al final de la llista">
            <i class="bi bi-plus-lg"></i> Afegir
          </button>
        </div>
        <div class="track-section js-section"></div>

        <div class="remaining js-remaining" role="button" title="Temps restant">--:--</div>
        <div class="seek-area js-seek" title="Clica o arrossega per saltar">
          <div class="progress player-progress" role="progressbar" aria-label="Posició">
            <div class="progress-bar js-progress"></div>
          </div>
        </div>
        <div class="times">
          <span class="js-position">0:00</span>
          <span class="track-format js-format"></span>
          <span class="js-duration">0:00</span>
        </div>

        <div class="js-meter-slot"></div>
        <div class="js-fader-slot"></div>

        <div class="controls">
          <button class="btn btn-outline-light btn-control btn-restart js-restart" type="button" title="Tornar a començar"><i class="bi bi-skip-start-fill"></i></button>
          <button class="btn btn-success btn-control js-play" type="button" title="Play"><i class="bi bi-play-fill"></i></button>
          <button class="btn btn-warning btn-control js-pause" type="button" title="Pausa"><i class="bi bi-pause-fill"></i></button>
          <button class="btn btn-danger btn-control js-stop" type="button" title="Stop"><i class="bi bi-stop-fill"></i></button>
          <button class="btn btn-outline-light btn-control js-retry d-none" type="button" title="Reintentar">
            <i class="bi bi-arrow-repeat"></i> Reintentar
          </button>
        </div>

        <div class="error-text js-error"></div>
        <div class="message-text js-message"></div>
        <div class="js-playlist-slot playlist-slot"></div>
        <div class="drop-hint">Deixa anar els fitxers per afegir-los al final de la llista</div>
      </div>`;

    for (const node of this.element.querySelectorAll('[class*="js-"]')) {
      const name = [...node.classList].find((c) => c.startsWith('js-'));
      if (name) this.#el[name.slice(3)] = /** @type {HTMLElement} */ (node);
    }

    this.#deviceSelect = new DeviceSelect('output', (deviceId) => this.#setDevice(deviceId));
    this.#el['device-slot'].appendChild(this.#deviceSelect.element);

    this.#meter = new LevelMeter();
    this.#el['meter-slot'].appendChild(this.#meter.element);
    this.#fader = new VolumeFader((db) => this.#send('player.setVolume', { db }));
    this.#el['fader-slot'].appendChild(this.#fader.element);
    this.#playlist = new PlaylistView(id, request, (text) => this.#showMessage(text), store);
    this.#el['playlist-slot'].appendChild(this.#playlist.element);

    this.#el.remaining.addEventListener('click', () => {
      this.#showElapsed = !this.#showElapsed;
      this.render();
    });
    // Les dreceres de teclat, a l'ajuda dels botons.
    const key = playKey(id);
    if (key) {
      this.#el.play.title = `Play (${key})`;
      this.#el.pause.title = `Pausa (${key})`;
      this.#el.stop.title = `Stop (Maj+${key})`;
    }
    this.#el.restart.addEventListener('click', () => this.#send('player.seek', { position: 0 }));
    this.#el.play.addEventListener('click', () => this.#send('player.play'));
    this.#el.pause.addEventListener('click', () => this.#pauseOrResume());
    this.#el.stop.addEventListener('click', () => this.#send('player.stop'));
    this.#setupSeek();
    this.#el.retry.addEventListener('click', () => this.#send('player.retry'));
    this.#el.open.addEventListener('click', () => this.#open());
    this.#el.edit.addEventListener('click', () => this.#edit());
    this.#setupDrop();

    this.render();
  }

  render() {
    const p = this.#store.players.get(this.#id);
    if (!p) return;

    const playing = p.state === 'playing';
    const el = this.#el;

    el.state.innerHTML = stateBadge(PLAYER_STATES[p.state]);
    this.#deviceSelect.update(this.#store, p.deviceId, playing);
    el['device-warning'].innerHTML = deviceWarning(this.#store, p.deviceId);

    // Pista actual: el nom que té a la llista (fitxer sense extensió).
    const list = this.#store.playlists.get(this.#id);
    const current = list?.items.find((/** @type {any} */ i) => i.id === p.currentItemId);
    const name = current?.name ?? (p.file ? p.file.split(/[\\/]/).pop()?.replace(/\.[^.]+$/, '') : null);
    el.track.textContent = name ?? (p.state === 'ended' ? 'Aturat' : 'Cap pista');
    el.track.title = p.file ?? '';

    // Secció de la llista on és la pista actual (D-042).
    const section = p.currentItemId ? sectionOf(list?.items ?? [], p.currentItemId) : null;
    el.section.className = `track-section js-section section-color-${section?.color ?? 'none'}${section ? '' : ' d-none'}`;
    el.section.innerHTML = section ? `<i class="bi bi-bookmark-fill"></i> ${escapeHtml(section.name)}` : '';
    el.section.title = section ? `Secció: ${section.name}` : '';
    el.format.textContent = p.format ?? '';
    el.format.title = p.format ?? '';

    // Mentre s'arrossega la barra, es mostra la posició on s'anirà.
    const hasTrack = p.duration > 0;
    const position = this.#dragFraction !== null ? this.#dragFraction * p.duration : p.position;

    // Temps restant (gran) i avís dels últims segons.
    const remaining = Math.max(0, p.duration - position);
    const remainingText = `−${formatDuration(Math.ceil(remaining))}`;
    const elapsedText = formatDuration(position);
    // El comptador gran mostra el temps restant o el transcorregut (es canvia clicant-hi);
    // el petit de sota, l'altre.
    el.remaining.textContent = !hasTrack ? '--:--' : this.#showElapsed ? elapsedText : remainingText;
    el.remaining.title = this.#showElapsed ? 'Temps transcorregut (clica per veure el restant)' : 'Temps restant (clica per veure el transcorregut)';
    // Avís de final (configurable; 0 = sense avís).
    const warning = this.#store.settings?.ui.endWarningSeconds ?? DEFAULT_END_WARNING_SECONDS;
    el.remaining.classList.toggle('ending', playing && warning > 0 && remaining <= warning);
    el.position.textContent = this.#showElapsed ? remainingText : elapsedText;
    el.duration.textContent = formatDuration(p.duration);
    el.progress.style.width = hasTrack ? `${Math.min(100, (position / p.duration) * 100)}%` : '0%';
    el.seek.classList.toggle('seekable', hasTrack && CAN_SEEK.includes(p.state));
    el.seek.classList.toggle('dragging', this.#dragFraction !== null);

    // Cada botó, només quan el motor l'accepta (docs/protocol.md, 4.2).
    setEnabled(el.restart, hasTrack && CAN_SEEK.includes(p.state));
    const hasItems = (list?.items.length ?? 0) > 0;
    setEnabled(el.play, ['ready', 'paused'].includes(p.state) || (['empty', 'ended'].includes(p.state) && hasItems));
    setEnabled(el.pause, playing || p.state === 'paused');
    el.pause.classList.toggle('blink', p.state === 'paused');
    setEnabled(el.stop, ['playing', 'paused', 'ready'].includes(p.state));
    el.retry.classList.toggle('d-none', p.state !== 'error');

    el.error.textContent = p.error ?? '';
    this.#fader.update(p.volumeDb ?? 0);
    this.#playlist.update(list, p);

    // Pista nova: l'indicador de saturació torna a començar (D-033).
    if (p.currentItemId !== this.#lastItemId) {
      this.#lastItemId = p.currentItemId;
      this.#meter.resetClip();
    }
  }

  /**
   * Botó ✂: obre a l'editor (fase 3) la pista actual del reproductor o, si no n'hi ha,
   * la primera seleccionada a la llista. L'editor només obre MP3.
   */
  #edit() {
    const p = this.#store.players.get(this.#id);
    const path = p?.file ?? this.#playlist.selectedPaths()[0];
    if (!path) {
      this.#showMessage('Selecciona una pista de la llista per obrir-la a l\'editor.');
      return;
    }
    if (!/\.mp3$/i.test(path)) {
      this.#showMessage('L\'editor només obre fitxers MP3.');
      return;
    }
    bridge?.openEditor(path);
  }

  /** Avís breu al panell (p. ex. una drecera de teclat que el motor ha rebutjat). */
  showMessage(/** @type {string} */ text) {
    this.#showMessage(text);
  }

  /** Ha canviat la llista (esdeveniment playlist.changed). */
  renderPlaylist() {
    this.render();
  }

  /** Nivells nous del motor (esdeveniment meters): [esquerre, dret] en dBFS. */
  updateMeter(/** @type {number[] | undefined} */ levels) {
    if (levels) this.#meter.update(levels);
  }

  // -------------------------------------------------------------------------

  /** Envia una ordre; si el motor la rebutja, en mostra el motiu uns segons. */
  async #send(/** @type {string} */ cmd, /** @type {object} */ params = {}) {
    try {
      await this.#request(cmd, { player: this.#id, ...params });
      return true;
    } catch (err) {
      this.#showMessage(/** @type {Error} */ (err).message);
      return false;
    }
  }

  async #setDevice(/** @type {string} */ deviceId) {
    if (!(await this.#send('player.setDevice', { deviceId }))) {
      this.#deviceSelect.invalidate();
      this.render();
    }
  }

  /** Pausa; si ja és en pausa, continua (com a Winamp). */
  #pauseOrResume() {
    const p = this.#store.players.get(this.#id);
    this.#send(p?.state === 'paused' ? 'player.play' : 'player.pause');
  }

  /**
   * Barra de posició: clicar-la o arrossegar-la per saltar (D-028).
   * Mentre s'arrossega només es mostra on anirà; el salt es fa en deixar anar el botó.
   */
  #setupSeek() {
    const bar = this.#el.seek;

    /** @param {PointerEvent} e */
    const fractionAt = (e) => {
      const rect = bar.getBoundingClientRect();
      return Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
    };

    bar.addEventListener('pointerdown', (e) => {
      const p = this.#store.players.get(this.#id);
      if (!p || !(p.duration > 0) || !CAN_SEEK.includes(p.state)) return;
      bar.setPointerCapture(e.pointerId);
      this.#dragFraction = fractionAt(e);
      this.render();
    });
    bar.addEventListener('pointermove', (e) => {
      if (this.#dragFraction === null) return;
      this.#dragFraction = fractionAt(e);
      this.render();
    });
    bar.addEventListener('pointerup', (e) => {
      if (this.#dragFraction === null) return;
      const p = this.#store.players.get(this.#id);
      const position = fractionAt(e) * (p?.duration ?? 0);
      this.#dragFraction = null;
      this.#send('player.seek', { position });
    });
    bar.addEventListener('pointercancel', () => {
      this.#dragFraction = null;
      this.render();
    });
  }

  /** Botó «Afegir»: diàleg de Windows (un o diversos fitxers), al final de la llista. */
  async #open() {
    const paths = await bridge?.openAudioFiles();
    if (paths?.length) this.#playlist.addFiles(paths);
  }

  /**
   * Fitxers de l'Explorador deixats anar a qualsevol lloc del panell que no sigui la llista:
   * s'afegeixen al final. (A sobre de la llista, ho gestiona la llista: on es deixen anar.)
   */
  #setupDrop() {
    const panel = this.element;
    panel.addEventListener('dragover', (e) => {
      if (!e.dataTransfer?.types.includes('Files')) return;
      e.preventDefault();
      panel.classList.add('drop-target');
    });
    panel.addEventListener('dragleave', (e) => {
      if (!panel.contains(/** @type {Node | null} */ (e.relatedTarget))) panel.classList.remove('drop-target');
    });
    panel.addEventListener('drop', (e) => {
      e.preventDefault();
      panel.classList.remove('drop-target');
      const paths = [...(e.dataTransfer?.files ?? [])].map((f) => bridge?.pathForFile(f)).filter(Boolean);
      this.#playlist.addFiles(paths);
    });
  }

  #showMessage(/** @type {string} */ text) {
    this.#el.message.textContent = text;
    clearTimeout(this.#messageTimer);
    this.#messageTimer = window.setTimeout(() => { this.#el.message.textContent = ''; }, MESSAGE_MS);
  }
}

/** @param {HTMLElement} button @param {boolean} enabled */
function setEnabled(button, enabled) {
  /** @type {HTMLButtonElement} */ (button).disabled = !enabled;
}
