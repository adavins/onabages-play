// @ts-check
// Panell de preescolta (especificació 7): escoltar una pista pels auriculars sense que surti a antena.
// La pista es tria amb el botó 🎧 de les llistes, de «Recents» o de l'enregistrament pendent (o la tecla P).
// La interfície només envia ordres (cue.*) i mostra l'estat que rep del motor.

import { deviceWarning } from './device-line.js';
import { DeviceSelect } from './device-select.js';
import { LevelMeter } from './meter.js';
import { VolumeFader } from './fader.js';
import { fileName, formatDuration } from './format.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** Salt dels botons −10 s i +10 s. */
const JUMP_SECONDS = 10;

/** «Últims 15 s»: per escoltar com acaba una pista. */
const LAST_SECONDS = 15;

/** Quant temps es mostra un missatge d'error d'una ordre. */
const MESSAGE_MS = 5000;

export class CuePanel {
  /** @type {HTMLElement} */ element;
  #store;
  #request;
  #deviceSelect;
  #meter;
  #fader;
  /** @type {Record<string, HTMLElement>} */ #el = {};
  /** @type {number | undefined} */ #messageTimer;
  /** Mentre s'arrossega la barra de posició: on es deixaria (0..1). Si no, null. */
  /** @type {number | null} */ #dragFraction = null;
  /** @type {string | null} */ #lastFile = null;
  /** El comptador gran mostra el temps transcorregut en lloc del restant (es canvia clicant-hi). */
  #showElapsed = false;

  /**
   * @param {Store} store
   * @param {Request} request  envia una ordre al motor
   */
  constructor(store, request) {
    this.#store = store;
    this.#request = request;

    this.element = document.createElement('section');
    this.element.className = 'card panel cue';
    this.element.setAttribute('aria-label', 'Preescolta');
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-title"><i class="bi bi-headphones"></i> Preescolta</span>
        <span class="js-state"></span>
      </div>
      <div class="card-body panel-body">
        <div class="js-device-slot"></div>
        <div class="js-device-warning"></div>
        <div class="cue-antenna js-antenna" role="alert">
          <i class="bi bi-broadcast"></i> La preescolta surt per antena
        </div>

        <div class="track js-track"></div>
        <div class="remaining cue-remaining js-remaining" role="button" title="Temps restant">--:--</div>
        <div class="seek-area js-seek" title="Clica o arrossega per saltar">
          <div class="progress player-progress cue-progress" role="progressbar" aria-label="Posició">
            <div class="progress-bar js-progress"></div>
          </div>
        </div>
        <div class="times">
          <span class="js-position">0:00</span>
          <span class="track-format js-format"></span>
          <span class="js-duration">0:00</span>
        </div>

        <div class="controls cue-controls">
          <button class="btn btn-outline-light btn-control cue-small js-start" type="button" title="Tornar a començar"><i class="bi bi-skip-start-fill"></i></button>
          <button class="btn btn-outline-light btn-control cue-small js-back" type="button" title="Enrere 10 s">−10</button>
          <button class="btn btn-control cue-toggle js-toggle" type="button" title="Preescoltar"><i class="bi bi-play-fill js-toggle-icon"></i></button>
          <button class="btn btn-outline-light btn-control cue-small js-forward" type="button" title="Endavant 10 s">+10</button>
          <button class="btn btn-outline-light btn-control cue-small js-end" type="button" title="Escoltar els últims ${LAST_SECONDS} s"><i class="bi bi-skip-end-fill"></i></button>
        </div>

        <div class="js-meter-slot"></div>
        <div class="js-fader-slot"></div>

        <div class="error-text js-error"></div>
        <div class="message-text js-message"></div>
        <div class="cue-hint js-hint">Prem <i class="bi bi-headphones"></i> en una pista (o la tecla P a la llista) per escoltar-la aquí.</div>
      </div>`;

    for (const node of this.element.querySelectorAll('[class*="js-"]')) {
      const name = [...node.classList].find((c) => c.startsWith('js-'));
      if (name) this.#el[name.slice(3)] = /** @type {HTMLElement} */ (node);
    }

    this.#deviceSelect = new DeviceSelect('output', (deviceId) => this.#setDevice(deviceId));
    this.#el['device-slot'].appendChild(this.#deviceSelect.element);
    this.#meter = new LevelMeter();
    this.#el['meter-slot'].appendChild(this.#meter.element);
    this.#fader = new VolumeFader((db) => this.#send('cue.setVolume', { db }),
      { min: -60, max: 0, icon: 'bi-headphones', label: 'Volum de la preescolta' });
    this.#el['fader-slot'].appendChild(this.#fader.element);

    this.#el.remaining.addEventListener('click', () => {
      this.#showElapsed = !this.#showElapsed;
      this.render();
    });
    this.#el.start.addEventListener('click', () => this.#seek(0));
    this.#el.back.addEventListener('click', () => this.#seek(this.#position() - JUMP_SECONDS));
    this.#el.forward.addEventListener('click', () => this.#seek(this.#position() + JUMP_SECONDS));
    this.#el.end.addEventListener('click', () => this.#seek((this.#store.cue?.duration ?? 0) - LAST_SECONDS));
    this.#el.toggle.addEventListener('click', () => this.#toggle());
    this.#setupSeek();

    this.render();
  }

  render() {
    const c = this.#store.cue;
    if (!c) return;
    const el = this.#el;
    const playing = c.state === 'playing';
    const hasFile = !!c.file;

    // Sonant: tot el panell en taronja i un rètol que parpelleja (es veu des de lluny).
    this.element.classList.toggle('cue-playing', playing);
    el.state.innerHTML = playing
      ? '<span class="state state-cue"><i class="bi bi-headphones"></i> PREESCOLTA</span>'
      : c.state === 'error'
        ? '<span class="state state-error"><i class="bi bi-exclamation-triangle-fill"></i> Error</span>'
        : '<span class="state state-idle"><i class="bi bi-stop-circle"></i> Aturada</span>';

    this.#deviceSelect.update(this.#store, c.deviceId, false);
    el['device-warning'].innerHTML = deviceWarning(this.#store, c.deviceId);
    el.antenna.classList.toggle('d-none', !this.#store.cueSharesPlayerOutput);

    el.track.textContent = hasFile ? fileName(c.file).replace(/\.[^.]+$/, '') : 'Cap pista';
    el.track.title = c.file ?? '';
    el.track.classList.toggle('dim', !hasFile);
    el.hint.classList.toggle('d-none', hasFile);
    el.format.textContent = c.format ?? '';
    el.format.title = c.format ?? '';

    // Temps: amb la preescolta aturada, la posició és 0 (es mostra la durada sencera).
    const duration = c.duration ?? 0;
    const position = this.#dragFraction !== null ? this.#dragFraction * duration : (c.position ?? 0);
    const remaining = Math.max(0, duration - position);
    const remainingText = `−${formatDuration(Math.ceil(remaining))}`;
    const elapsedText = formatDuration(position);
    // El comptador gran mostra el restant o el transcorregut (es canvia clicant-hi); el petit de sota, l'altre.
    el.remaining.textContent = duration <= 0 ? '--:--' : this.#showElapsed ? elapsedText : remainingText;
    el.remaining.title = this.#showElapsed ? 'Temps transcorregut (clica per veure el restant)' : 'Temps restant (clica per veure el transcorregut)';
    el.remaining.classList.toggle('dim', !playing);
    el.position.textContent = this.#showElapsed ? remainingText : elapsedText;
    el.duration.textContent = formatDuration(duration);
    el.progress.style.width = duration > 0 ? `${Math.min(100, (position / duration) * 100)}%` : '0%';
    const canUse = hasFile && !!c.deviceId;
    el.seek.classList.toggle('seekable', canUse && duration > 0);
    el.seek.classList.toggle('dragging', this.#dragFraction !== null);

    // Botons. ▶ torna a fer sonar l'última pista des del principi; ⏹ l'atura.
    for (const name of ['start', 'back', 'forward', 'end', 'toggle']) setEnabled(el[name], canUse);
    // Només es canvia la classe de la icona: si es substituís l'element (10 vegades per segon mentre sona),
    // el clic es perdria quan canvia entre prémer i deixar anar el botó del ratolí.
    el['toggle-icon'].className = `bi ${playing ? 'bi-stop-fill' : 'bi-play-fill'}`;
    el.toggle.title = playing ? 'Aturar la preescolta' : 'Preescoltar des del principi';
    el.toggle.classList.toggle('btn-danger', playing);
    el.toggle.classList.toggle('btn-cue', !playing);

    el.error.textContent = c.error ?? '';
    this.#fader.update(c.volumeDb ?? 0);

    // Pista nova: l'indicador de saturació torna a començar.
    if (c.file !== this.#lastFile) {
      this.#lastFile = c.file;
      this.#meter.resetClip();
    }
  }

  /** Nivells nous del motor (esdeveniment meters): [esquerre, dret] en dBFS. */
  updateMeter(/** @type {number[] | undefined} */ levels) {
    if (levels) this.#meter.update(levels);
  }

  // -------------------------------------------------------------------------

  #position() {
    return this.#store.cue?.position ?? 0;
  }

  /** Salta a un punt (dins la pista). Si està aturada, el motor la fa sonar des d'allà. */
  #seek(/** @type {number} */ seconds) {
    const duration = this.#store.cue?.duration ?? 0;
    const position = Math.max(0, Math.min(seconds, Math.max(0, duration - 0.5)));
    this.#send('cue.seek', { position });
  }

  #toggle() {
    this.#send(this.#store.cue?.state === 'playing' ? 'cue.stop' : 'cue.play');
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
    if (!(await this.#send('cue.setDevice', { deviceId }))) {
      this.#deviceSelect.invalidate();
      this.render();
    }
  }

  /** Barra de posició: clicar-la o arrossegar-la per saltar (com als reproductors). */
  #setupSeek() {
    const bar = this.#el.seek;

    /** @param {PointerEvent} e */
    const fractionAt = (e) => {
      const rect = bar.getBoundingClientRect();
      return Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
    };

    bar.addEventListener('pointerdown', (e) => {
      if (!bar.classList.contains('seekable')) return;
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
      const position = fractionAt(e) * (this.#store.cue?.duration ?? 0);
      this.#dragFraction = null;
      this.#seek(position);
    });
    bar.addEventListener('pointercancel', () => {
      this.#dragFraction = null;
      this.render();
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
