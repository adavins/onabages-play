// @ts-check
// Panell d'un reproductor: pista actual, controls, temps, sortida, volum i llista.
// La interfície només envia ordres i mostra l'estat que rep del motor.

import { PLAYER_STATES, stateBadge } from './states.js';
import { PATHS_DRAG_TYPE, draggedPaths } from './drag-types.js';
import { deviceWarning } from './device-line.js';
import { DeviceSelect } from './device-select.js';
import { LevelMeter } from './meter.js';
import { VolumeFader } from './fader.js';
import { PlaylistView, sectionOf } from './playlist-view.js';
import { escapeHtml, formatDuration, clockText } from './format.js';
import { channelState } from './channels.js';
import { HANDLE_HTML } from './panel-order.js';
import { openProcessingDialog } from './processing-dialog.js';

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
  /** Hora d'acabar que es mostra (ms des de 1970), o null si no sona. Vegeu #renderEndTime. @type {number | null} */
  #shownEnd = null;
  /** Reducció del compressor que es mostra (amb caiguda suau). */
  #shownReduction = 0;

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
    this.element.dataset.panelId = id; // per canviar-lo de lloc (panel-order.js)
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-header-left">${HANDLE_HTML}<span class="panel-title"><i class="bi bi-music-note-list"></i> Reproductor ${escapeHtml(id)}</span></span>
        <span class="panel-header-right">
          <button class="btn btn-sm dsp-btn norm-btn js-norm" type="button"><i class="d-none"></i><span class="norm-label">NORM</span></button>
          <button class="btn btn-sm dsp-btn level-btn js-level" type="button"><i class="d-none"></i><span>NIV</span></button>
          <button class="btn btn-sm dsp-btn js-dsp" type="button"><i class="d-none"></i><span class="dsp-label">DSP</span></button>
          <span class="js-state"></span>
        </span>
      </div>
      <div class="card-body panel-body">
        <div class="js-device-slot"></div>
        <div class="js-device-warning"></div>

        <div class="track-row">
          <div class="track-section js-section"></div>
          <div class="track js-track"></div>
          <button class="btn btn-sm btn-outline-secondary js-edit" type="button" title="Obrir a l'editor la pista actual (o la seleccionada a la llista)">
            <i class="bi bi-scissors"></i>
          </button>
          <button class="btn btn-sm btn-outline-secondary js-open" type="button" title="Afegir pistes al final de la llista">
            <i class="bi bi-plus-lg"></i> 
          </button>
        </div>

        <div class="remaining-row">
          <button class="btn btn-sm dsp-btn mono-btn js-mono" type="button" aria-pressed="false">
            <i class="bi bi-exclamation-triangle-fill d-none js-mono-warn"></i><span>MONO</span>
          </button>
          <div class="remaining js-remaining" role="button" title="Temps restant">--:--</div>
          <div class="end-time js-endtime">
            <span class="end-time-label">Acaba</span>
            <span class="end-time-value js-endvalue">--:--:--</span>
          </div>
        </div>
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
        <div class="gr-row js-gr d-none" title="Quant abaixa ara el compressor/limitador">
          <span class="gr-caption">Reducció</span>
          <div class="gr-bar"><div class="gr-fill js-gr-fill"></div></div>
          <span class="gr-value js-gr-value">0,0 dB</span>
        </div>
        <div class="js-fader-slot"></div>

        <div class="controls">
          <button class="btn btn-outline-light btn-control btn-restart js-restart" type="button" title="Tornar a començar"><i class="bi bi-skip-start-fill"></i></button>
          <button class="btn btn-success btn-control js-play" type="button" title="Play"><i class="bi bi-play-fill"></i></button>
          <button class="btn btn-warning btn-control js-pause" type="button" title="Pausa"><i class="bi bi-pause-fill"></i></button>
          <button class="btn btn-danger btn-control js-stop" type="button" title="Stop"><i class="bi bi-stop-fill"></i></button>
          <button class="btn btn-outline-danger btn-control btn-fade js-fade" type="button" title="Fade-out"><span>FADE</span></button>
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

    this.#el.mono.addEventListener('click', () => this.#toggleMono());
    this.#el.remaining.addEventListener('click', () => {
      this.#showElapsed = !this.#showElapsed;
      this.render();
    });
    this.#el.restart.addEventListener('click', () => this.#send('player.seek', { position: 0 }));
    this.#el.play.addEventListener('click', () => this.#send('player.play'));
    this.#el.pause.addEventListener('click', () => this.#pauseOrResume());
    this.#el.stop.addEventListener('click', () => this.#send('player.stop'));
    // Fade-out (pas 20): el so baixa i el reproductor s'atura sol. No demana confirmació, com Stop.
    this.#el.fade.addEventListener('click', () => this.#send('player.fadeOut'));
    this.#setupSeek();
    this.#el.retry.addEventListener('click', () => this.#send('player.retry'));
    this.#el.open.addEventListener('click', () => this.#open());
    this.#el.edit.addEventListener('click', () => this.#edit());
    this.#el.dsp.addEventListener('click', () => openProcessingDialog(this.#store, this.#request, this.#id));
    // NIV (fase 5, pas 30c; D-136): l'anivellador. Amb una pista carregada s'aplica de seguida (el motor la torna a obrir on és).
    this.#el.level.addEventListener('click', () => {
      const enabled = !!this.#store.players.get(this.#id)?.leveler?.enabled;
      this.#send('player.setLeveler', { enabled: !enabled });
    });
    // NORM: activa o desactiva la normalització (a partir de la pista següent).
    this.#el.norm.addEventListener('click', () => {
      const enabled = !!this.#store.players.get(this.#id)?.normalization?.enabled;
      this.#send('player.setNormalization', { enabled: !enabled });
      if (!enabled && this.#store.players.get(this.#id)?.state === 'playing') {
        this.#showMessage('Normalització activada: s\'aplicarà a partir de la pista següent.');
      }
    });
    this.#setupDrop();

    this.render();
  }

  render() {
    const p = this.#store.players.get(this.#id);
    if (!p) return;

    const playing = p.state === 'playing';
    const el = this.#el;

    el.state.innerHTML = stateBadge(PLAYER_STATES[p.state]);

    // Compressor/limitador (D-068): botó encès (color i marca ✓) i indicador de reducció.
    const dsp = p.processing ?? {};
    el.dsp.classList.toggle('on', !!dsp.enabled);
    el.dsp.setAttribute('aria-pressed', String(!!dsp.enabled));
    el.dsp.title = dsp.enabled
      ? `Compressor/limitador ACTIVAT (guany ${dsp.inputGainDb} dB, llindar ${dsp.thresholdDb} dB, ràtio ${dsp.ratio}:1). Clica per canviar-lo.`
      : 'Compressor/limitador desactivat. Clica per configurar-lo.';
    // El fos entre pistes es configura a la mateixa finestra (fase 5).
    if (p.crossfadeMs > 0) el.dsp.title += ` Fos entre pistes encadenades: ${p.crossfadeMs} ms.`;
    if (p.trim?.enabled) el.dsp.title += ' Retall de silencis activat.';
    // Activat: ple de color i amb una marca ✓ (el text no canvia, perquè la capçalera és estreta).
    /** @type {HTMLElement} */ (el.dsp.querySelector('i')).className = dsp.enabled ? 'bi bi-check-lg' : 'd-none';
    el.gr.classList.toggle('d-none', !dsp.enabled);

    // Normalització (D-072): botó encès (color i marca ✓).
    const norm = p.normalization ?? {};
    el.norm.classList.toggle('on', !!norm.enabled);
    el.norm.setAttribute('aria-pressed', String(!!norm.enabled));
    const target = String(norm.targetLufs ?? -12).replace('-', '−');
    el.norm.title = norm.enabled
      ? `Normalització ACTIVADA: cada pista s'iguala a ${target} LUFS abans del DSP. Clica per desactivar-la (a partir de la pista següent).`
      : `Normalització desactivada. Clica per igualar el volum de les pistes a ${target} LUFS (a partir de la pista següent).`;
    /** @type {HTMLElement} */ (el.norm.querySelector('i')).className = norm.enabled ? 'bi bi-check-lg' : 'd-none';

    // Anivellador (D-136): botó encès (color i marca ✓). Amb el NIV, el NORM no s'aplica: es diu a l'ajuda del NORM.
    const level = p.leveler ?? {};
    el.level.classList.toggle('on', !!level.enabled);
    el.level.setAttribute('aria-pressed', String(!!level.enabled));
    el.level.title = level.enabled
      ? `Anivellador ACTIVAT: iguala el volum de la parla mentre sona (p. ex. una entrevista amb una veu fluixa i una de forta), cap a ${target} LUFS, com a molt +${level.maxBoostDb ?? 12} dB. Clica per desactivar-lo (de seguida).`
      : `Anivellador desactivat. Clica per igualar el volum de la parla mentre sona (cap a ${target} LUFS). S'aplica de seguida, també a la pista que sona.`;
    /** @type {HTMLElement} */ (el.level.querySelector('i')).className = level.enabled ? 'bi bi-check-lg' : 'd-none';
    if (level.enabled && norm.enabled) el.norm.title += '\nAmb el NIV activat, la normalització no s\'aplica (l\'anivellador ja porta la pista a aquest nivell).';
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
    // Format de la pista i, si es normalitza, el guany que se li aplica (p. ex. «· Norm. +7,2 dB»).
    const normGain = p.normalization?.gainDb;
    const normText = typeof normGain === 'number'
      ? `Norm. ${normGain > 0 ? '+' : normGain < 0 ? '−' : ''}${Math.abs(normGain).toFixed(1).replace('.', ',')} dB`
      : '';
    // Anivellador: el guany del moment que sona (10 vegades per segon, de player.position), p. ex. «Niv. +8,5 dB».
    const levelGain = p.levelerGainDb !== undefined ? p.levelerGainDb : p.leveler?.gainDb;
    const levelText = typeof levelGain === 'number'
      ? `Niv. ${levelGain > 0 ? '+' : levelGain < 0 ? '−' : ''}${Math.abs(levelGain).toFixed(1).replace('.', ',')} dB`
      : '';
    el.format.textContent = [p.format, normText, levelText].filter(Boolean).join(' · ');
    el.format.title = el.format.textContent;

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
    // Hora d'acabar (pas 18). Si la pista que sona està en bucle, no s'acaba sola.
    const looping = !!this.#store.playlists.get(this.#id)?.items?.find((/** @type {any} */ i) => i.id === p.currentItemId)?.loop;
    // (Mentre s'arrossega la barra, l'hora és la d'acabar si es deixa anar allà.)
    this.#renderEndTime(playing && hasTrack, looping, remaining);
    el.position.textContent = this.#showElapsed ? remainingText : elapsedText;
    el.duration.textContent = formatDuration(p.duration);
    el.progress.style.width = hasTrack ? `${Math.min(100, (position / p.duration) * 100)}%` : '0%';
    el.seek.classList.toggle('seekable', hasTrack && CAN_SEEK.includes(p.state));
    el.seek.classList.toggle('dragging', this.#dragFraction !== null);
    this.#renderMono(p);

    // Cada botó, només quan el motor l'accepta (docs/protocol.md, 4.2).
    setEnabled(el.restart, hasTrack && CAN_SEEK.includes(p.state));
    const hasItems = (list?.items.length ?? 0) > 0;
    setEnabled(el.play, ['ready', 'paused'].includes(p.state) || (['empty', 'ended'].includes(p.state) && hasItems));
    setEnabled(el.pause, playing || p.state === 'paused');
    el.pause.classList.toggle('blink', p.state === 'paused');
    setEnabled(el.stop, ['playing', 'paused', 'ready'].includes(p.state));
    // Fade-out: només mentre sona. Mentre baixa, el botó parpelleja (Stop l'atura de cop).
    const fadeSeconds = String((this.#store.settings?.fadeOut?.ms ?? 2000) / 1000).replace('.', ',');
    setEnabled(el.fade, playing);
    el.fade.classList.toggle('fading', playing && !!p.fadingOut);
    el.fade.title = playing && p.fadingOut
      ? "Baixant… En acabar, s'atura sol. Per aturar de cop, Stop."
      : `Fade-out: baixa el so en ${fadeSeconds} s i després s'atura`;
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

  /**
   * Hora prevista d'acabar la pista que sona (pas 18): ara + temps restant, amb segons.
   * Només mentre sona: en pausa o aturat, l'hora no se sap (depèn de quan es torni a prémer Play).
   * La posició arriba del motor unes quantes vegades per segon i cada càlcul pot variar unes dècimes:
   * l'hora mostrada només es canvia si la nova difereix més de 0,75 s (un salt, una pausa...), perquè
   * no salti d'un segon a l'altre sense motiu.
   * @param {boolean} playing @param {boolean} loop @param {number} remaining  segons
   */
  #renderEndTime(playing, loop, remaining) {
    const el = this.#el;
    if (!playing || loop) {
      this.#shownEnd = null;
      el.endvalue.textContent = loop && playing ? '∞ bucle' : '--:--:--';
      el.endtime.classList.add('idle');
      el.endtime.title = loop && playing ? "La pista es repeteix: no s'acaba sola" : "Hora prevista d'acabar la pista (mentre sona)";
      return;
    }
    const end = Date.now() + remaining * 1000;
    if (this.#shownEnd === null || Math.abs(end - this.#shownEnd) > 750) this.#shownEnd = end;
    const text = clockText(this.#shownEnd);
    el.endvalue.textContent = text;
    el.endtime.classList.remove('idle');
    el.endtime.title = `La pista acabarà a les ${text}`;
  }

  /** Avís breu al panell (p. ex. una drecera de teclat que el motor ha rebutjat). */
  /**
   * La drecera d'aquest reproductor (p. ex. «F1»), a l'ajuda dels botons. Va per la posició a la pantalla
   * (D-105): canvia quan el reproductor es mou de lloc. Buit = cap.
   */
  setKey(/** @type {string} */ key) {
    this.#el.play.title = key ? `Play (${key})` : 'Play';
    this.#el.pause.title = key ? `Pausa (${key})` : 'Pausa';
    this.#el.stop.title = key ? `Stop (Maj+${key})` : 'Stop';
  }

  showMessage(/** @type {string} */ text) {
    this.#showMessage(text);
  }

  /** Ha canviat la llista (esdeveniment playlist.changed). */
  /** La pista carregada (la que sona, en pausa o a punt), o undefined. */
  #currentItem(/** @type {any} */ p) {
    return this.#store.playlists.get(this.#id)?.items?.find((/** @type {any} */ i) => i.id === p.currentItemId);
  }

  /**
   * Botó MONO (pas 29b; D-133): encès si la pista carregada sona pels dos canals (un canal buit corregit sol, o activat a
   * mà); amb ⚠ si té un canal buit i s'ha desactivat la correcció.
   */
  #renderMono(/** @type {any} */ p) {
    const button = this.#el.mono;
    const item = this.#currentItem(p);
    const { on, warn, text } = channelState(item);
    button.classList.toggle('on', on);
    button.classList.toggle('mono-warn', warn);
    button.setAttribute('aria-pressed', String(on));
    this.#el['mono-warn'].classList.toggle('d-none', !warn);
    /** @type {HTMLButtonElement} */ (button).disabled = !item;
    button.title = !item
      ? 'MONO: fer sonar la pista pels dos canals (quan n\'hi hagi una de carregada)'
      : `${text}\nClica per ${on ? 'deixar-la tal com és (estèreo)' : 'fer-la sonar pels dos canals'}. Es recorda per a aquesta pista de la llista.`;
  }

  /** MONO: activa o desactiva la correcció de la pista carregada (de seguida, també si sona). */
  #toggleMono() {
    const p = this.#store.players.get(this.#id);
    const item = p && this.#currentItem(p);
    if (!item) return;
    this.#send('playlist.setFlags', { itemId: item.id, bothChannels: !channelState(item).on });
  }

  renderPlaylist() {
    this.render();
  }

  /** Nivells nous del motor (esdeveniment meters): [esquerre, dret] en dBFS. */
  updateMeter(/** @type {number[] | undefined} */ levels, /** @type {number | undefined} */ reduction) {
    if (levels) this.#meter.update(levels);
    // Reducció del compressor, amb una caiguda suau perquè es pugui llegir (escala de 0 a 20 dB).
    if (reduction !== undefined && !this.#el.gr.classList.contains('d-none')) {
      this.#shownReduction = Math.max(reduction, this.#shownReduction - 0.4);
      this.#el['gr-fill'].style.width = `${Math.min(100, (this.#shownReduction / 20) * 100)}%`;
      this.#el['gr-value'].textContent = `${this.#shownReduction.toFixed(1).replace('.', ',')} dB`;
    }
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
    // Abans que ningú (fase de captura) es treu l'avís; el gestor de sota el torna a posar si el
    // fitxer és fora de la llista. A sobre de la llista, la llista atura l'esdeveniment (mostra la
    // seva pròpia marca i rep el fitxer): sense això, l'avís es quedava posat després de deixar-lo anar.
    const hideHint = () => panel.classList.remove('drop-target');
    panel.addEventListener('dragover', hideHint, true);
    panel.addEventListener('drop', hideHint, true);
    panel.addEventListener('dragover', (e) => {
      const types = e.dataTransfer?.types ?? [];
      if (!types.includes('Files') && !types.includes(PATHS_DRAG_TYPE)) return;
      e.preventDefault();
      panel.classList.add('drop-target');
    });
    panel.addEventListener('dragleave', (e) => {
      if (!panel.contains(/** @type {Node | null} */ (e.relatedTarget))) panel.classList.remove('drop-target');
    });
    panel.addEventListener('drop', (e) => {
      e.preventDefault();
      panel.classList.remove('drop-target');
      const fromExplorer = e.dataTransfer ? draggedPaths(e.dataTransfer) : [];
      const paths = fromExplorer.length > 0
        ? fromExplorer
        : [...(e.dataTransfer?.files ?? [])].map((f) => bridge?.pathForFile(f)).filter(Boolean);
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
