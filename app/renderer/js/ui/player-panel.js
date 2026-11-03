// @ts-check
// Panell d'un reproductor (pas 2): una pista, controls, temps i sortida.
// La interfície només envia ordres i mostra l'estat que rep del motor.

import { PLAYER_STATES, stateBadge } from './states.js';
import { deviceWarning } from './device-line.js';
import { DeviceSelect } from './device-select.js';
import { LevelMeter } from './meter.js';
import { VolumeFader } from './fader.js';
import { escapeHtml, fileName, formatDuration } from './format.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** Segons abans del final en què el temps restant canvia d'aspecte (especificació 6.2). Configurable més endavant. */
const END_WARNING_SECONDS = 10;

/** Estats en què es pot saltar dins la pista (player.seek). */
const CAN_SEEK = ['ready', 'playing', 'paused', 'ended'];

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
          <button class="btn btn-sm btn-outline-secondary js-open" type="button" title="Obrir una pista">
            <i class="bi bi-folder2-open"></i> Obrir
          </button>
        </div>

        <div class="remaining js-remaining" role="button" title="Temps restant">--:--</div>
        <div class="seek-area js-seek" title="Clica o arrossega per saltar">
          <div class="progress player-progress" role="progressbar" aria-label="Posició">
            <div class="progress-bar js-progress"></div>
          </div>
        </div>
        <div class="times">
          <span class="js-position">0:00</span>
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
        <div class="drop-hint">Deixa anar el fitxer per carregar-lo</div>
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

    this.#el.remaining.addEventListener('click', () => {
      this.#showElapsed = !this.#showElapsed;
      this.render();
    });
    this.#el.restart.addEventListener('click', () => this.#send('player.seek', { position: 0 }));
    this.#el.play.addEventListener('click', () => this.#send('player.play'));
    this.#el.pause.addEventListener('click', () => this.#pauseOrResume());
    this.#el.stop.addEventListener('click', () => this.#send('player.stop'));
    this.#setupSeek();
    this.#el.retry.addEventListener('click', () => this.#send('player.retry'));
    this.#el.open.addEventListener('click', () => this.#open());
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

    el.track.textContent = p.file ? fileName(p.file) : 'Cap pista carregada';
    el.track.title = p.file ?? '';

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
    el.remaining.classList.toggle('ending', playing && remaining <= END_WARNING_SECONDS);
    el.position.textContent = this.#showElapsed ? remainingText : elapsedText;
    el.duration.textContent = formatDuration(p.duration);
    el.progress.style.width = hasTrack ? `${Math.min(100, (position / p.duration) * 100)}%` : '0%';
    el.seek.classList.toggle('seekable', hasTrack && CAN_SEEK.includes(p.state));
    el.seek.classList.toggle('dragging', this.#dragFraction !== null);

    // Cada botó, només quan el motor l'accepta (docs/protocol.md, 4.2).
    setEnabled(el.restart, hasTrack && CAN_SEEK.includes(p.state));
    setEnabled(el.play, ['ready', 'paused', 'ended'].includes(p.state));
    setEnabled(el.pause, playing || p.state === 'paused');
    el.pause.classList.toggle('blink', p.state === 'paused');
    setEnabled(el.stop, ['playing', 'paused', 'ended'].includes(p.state));
    setEnabled(el.open, !playing && !!p.deviceId);
    el.retry.classList.toggle('d-none', p.state !== 'error');

    el.error.textContent = p.error ?? '';
    this.#fader.update(p.volumeDb ?? 0);
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

  async #open() {
    const path = await bridge?.openAudioFile();
    if (path) await this.#load(path);
  }

  async #load(/** @type {string} */ path) {
    const p = this.#store.players.get(this.#id);
    if (p?.state === 'playing') {
      this.#showMessage('No es pot carregar una pista mentre sona. Atura-la primer.');
      return;
    }
    await this.#send('player.load', { path });
  }

  /** Arrossegar un fitxer des de l'Explorador al panell. */
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
      const file = e.dataTransfer?.files[0];
      if (!file) return;
      const path = bridge?.pathForFile(file);
      if (path) this.#load(path);
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
