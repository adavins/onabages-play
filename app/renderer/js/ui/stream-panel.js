// @ts-check
// Emissor d'streaming (fase 5, pas 31d; D-140 a D-143): una pestanya del panell lateral, com el BUTT.
// La interfície només envia ordres (stream.*) i mostra l'estat que rep del motor.
//
// - Servidor (de la llista del perfil, que es gestiona amb el botó del costat), font, mesurador i fader (−60 a +12 dB).
// - NIV, DSP i LIM, com als reproductors i als enregistradors: es poden canviar mentre s'emet.
// - Qualitat (bitrate, freqüència, mono): només amb l'emissió aturada.
// - Títol que veuen els oients: s'envia en prémer Retorn o en sortir del camp (també mentre s'emet).
// - Inicia l'emissió (sense confirmació) i Atura l'emissió (amb confirmació: interromp el que senten els oients).
// - Si la connexió es talla, el motor es torna a connectar sol; aquí es diu què passa i per què.

import { STREAM_STATES, stateBadge } from './states.js';
import { deviceWarning } from './device-line.js';
import { DeviceSelect } from './device-select.js';
import { LevelMeter } from './meter.js';
import { VolumeFader } from './fader.js';
import { openStreamProcessingDialog } from './processing-dialog.js';
import { manageStreamServers } from './stream-servers-dialog.js';
import { choose } from './dialogs.js';
import { escapeHtml, formatBytes, formatDuration } from './format.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

const MESSAGE_MS = 6000;

/** Escala de la barra de reducció (dB). */
const REDUCTION_SCALE_DB = 20;

/** Estats en què s'està emetent (o intentant-ho). */
const ACTIVE = ['connecting', 'streaming', 'reconnecting'];

export class StreamPanel {
  /** @type {HTMLElement} */ element;
  #store;
  #request;
  #deviceSelect;
  #meter;
  #fader;
  /** @type {Record<string, HTMLElement>} */ #el = {};
  /** @type {number | undefined} */ #messageTimer;
  #serversKey = '';
  #qualityKey = '';
  #shownReduction = 0;
  /** L'estat de l'últim render (per reiniciar l'indicador de saturació en començar). */
  #lastState = '';

  /**
   * @param {Store} store
   * @param {Request} request  envia una ordre al motor
   */
  constructor(store, request) {
    this.#store = store;
    this.#request = request;

    this.element = document.createElement('section');
    this.element.id = 'stream';
    this.element.className = 'card panel stream';
    this.element.setAttribute('aria-label', 'Streaming');
    this.element.innerHTML = `
      <div class="card-header panel-header">
        <span class="panel-title"><i class="bi bi-broadcast"></i> Streaming</span>
        <span class="panel-header-right">
          <button class="btn btn-sm dsp-btn level-btn js-level" type="button"><i class="d-none"></i><span>NIV</span></button>
          <button class="btn btn-sm dsp-btn js-dsp" type="button"><i class="d-none"></i><span>DSP</span></button>
          <button class="btn btn-sm dsp-btn js-lim" type="button"><i class="d-none"></i><span>LIM</span></button>
          <span class="js-state"></span>
        </span>
      </div>
      <div class="card-body panel-body">
        <div class="stream-row">
          <label class="stream-label" for="stream-server">Servidor</label>
          <select id="stream-server" class="form-select form-select-sm js-server"></select>
          <button class="btn btn-sm btn-outline-secondary js-servers" type="button" title="Servidors: afegir, canviar o esborrar">
            <i class="bi bi-hdd-network"></i></button>
        </div>
        <div class="stream-row">
          <span class="stream-label">Font</span>
          <div class="stream-device js-device-slot"></div>
        </div>
        <div class="js-device-warning"></div>
        <div class="js-meter-slot"></div>
        <div class="input-off js-input-off">Font no disponible: el mesurador s'activarà quan torni.</div>
        <div class="js-fader-slot"></div>
        <div class="gr-row js-gr d-none" title="Quant abaixen ara el compressor (DSP) i el limitador (LIM), mentre s'emet">
          <span class="gr-caption">Reducció</span>
          <div class="gr-bar"><div class="gr-fill js-gr-fill"></div></div>
          <span class="gr-value js-gr-value">0,0 dB</span>
        </div>

        <div class="stream-row">
          <span class="stream-label">Qualitat</span>
          <select class="form-select form-select-sm js-bitrate" aria-label="Bitrate"></select>
          <select class="form-select form-select-sm js-rate" aria-label="Freqüència"></select>
          <div class="form-check stream-mono">
            <input class="form-check-input js-mono" type="checkbox" id="stream-mono">
            <label class="form-check-label" for="stream-mono">Mono</label>
          </div>
        </div>
        <div class="stream-row">
          <label class="stream-label" for="stream-title">Títol</label>
          <input id="stream-title" class="form-control form-control-sm js-title" type="text"
                 placeholder="p. ex. Directe des de la plaça" title="El que veuen els oients. Retorn: enviar-lo">
        </div>
        <div class="stream-warning js-title-error"></div>

        <div class="rec-info stream-info">
          <div class="elapsed js-elapsed">0:00</div>
          <div class="rec-file">
            <div class="track js-where"></div>
            <div class="rec-sizes js-details"></div>
          </div>
        </div>
        <div class="stream-warning js-dropped"></div>
        <div class="stream-warning js-connection" role="status"></div>

        <div class="controls">
          <button class="btn btn-control stream-start js-start" type="button" title="Començar a emetre">
            <i class="bi bi-broadcast"></i><span> Inicia l'emissió</span>
          </button>
          <button class="btn btn-outline-light btn-control stream-stop js-stop" type="button" title="Aturar l'emissió (demana confirmació)">
            <i class="bi bi-stop-fill"></i><span> Atura</span>
          </button>
        </div>

        <div class="error-text js-error"></div>
        <div class="message-text js-message"></div>
      </div>`;

    for (const node of this.element.querySelectorAll('[class*="js-"]')) {
      const name = [...node.classList].find((c) => c.startsWith('js-'));
      if (name) this.#el[name.slice(3)] = /** @type {HTMLElement} */ (node);
    }

    this.#deviceSelect = new DeviceSelect('input', (deviceId) => this.#setDevice(deviceId));
    this.#el['device-slot'].appendChild(this.#deviceSelect.element);
    this.#meter = new LevelMeter();
    this.#el['meter-slot'].appendChild(this.#meter.element);
    this.#fader = new VolumeFader((db) => this.#send('stream.setGain', { db }),
      { min: -60, max: 12, icon: 'bi-broadcast', label: 'Volum de l\'emissió' });
    this.#el['fader-slot'].appendChild(this.#fader.element);

    this.#el.server.addEventListener('change', () => {
      const serverId = /** @type {HTMLSelectElement} */ (this.#el.server).value;
      if (serverId) this.#send('stream.selectServer', { serverId }).then((ok) => { if (!ok) { this.#serversKey = ''; this.render(); } });
    });
    this.#el.servers.addEventListener('click', () => manageStreamServers(this.#store, this.#request));
    this.#el.level.addEventListener('click', () => this.#send('stream.setLeveler', { enabled: !this.#store.stream?.leveler?.enabled }));
    this.#el.dsp.addEventListener('click', () => openStreamProcessingDialog(this.#store, this.#request));
    this.#el.lim.addEventListener('click', () => this.#send('stream.setLimiter', { enabled: !this.#store.stream?.limiter?.enabled }));
    const sendQuality = () => this.#send('stream.setQuality', {
      bitrateKbps: Number(/** @type {HTMLSelectElement} */ (this.#el.bitrate).value),
      sampleRate: Number(/** @type {HTMLSelectElement} */ (this.#el.rate).value),
      mono: /** @type {HTMLInputElement} */ (this.#el.mono).checked,
    }).then((ok) => { if (!ok) { this.#qualityKey = ''; this.render(); } });
    this.#el.bitrate.addEventListener('change', sendQuality);
    this.#el.rate.addEventListener('change', sendQuality);
    this.#el.mono.addEventListener('change', sendQuality);

    // Títol: Retorn l'envia; Esc torna al que hi ha; en sortir del camp, s'envia si ha canviat.
    const title = /** @type {HTMLInputElement} */ (this.#el.title);
    title.maxLength = 120;
    title.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); this.#sendTitle(); title.blur(); }
      else if (e.key === 'Escape') { title.value = this.#store.stream?.title ?? ''; title.blur(); }
    });
    title.addEventListener('blur', () => this.#sendTitle());

    this.#el.start.addEventListener('click', () => this.#start());
    this.#el.stop.addEventListener('click', () => this.#stop());

    this.render();
  }

  render() {
    const s = this.#store.stream;
    if (!s) return;
    const el = this.#el;
    const active = ACTIVE.includes(s.state);

    this.element.classList.toggle('stream-active', s.state === 'streaming');
    this.element.classList.toggle('stream-trying', s.state === 'connecting' || s.state === 'reconnecting');
    el.state.innerHTML = stateBadge(STREAM_STATES[s.state]);
    if (s.state === 'streaming' && this.#lastState !== 'streaming') this.#meter.resetClip();
    this.#lastState = s.state;

    this.#renderServers(s, active);
    this.#deviceSelect.update(this.#store, s.deviceId, active);
    el['device-warning'].innerHTML = deviceWarning(this.#store, s.deviceId);
    const inputOff = !!s.deviceId && !s.inputActive;
    this.#meter.setActive(!inputOff);
    el['input-off'].classList.toggle('d-none', !inputOff || s.state === 'error');
    this.#fader.update(s.gainDb ?? 0);
    this.#renderProcessing(s);
    this.#renderQuality(s, active);

    const title = /** @type {HTMLInputElement} */ (el.title);
    if (document.activeElement !== title) title.value = s.title ?? '';
    el['title-error'].innerHTML = s.titleError
      ? `<i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(s.titleError)}` : '';

    this.renderProgress();

    // Connexió: mentre es connecta o es reconnecta, per què ha fallat l'últim intent.
    const connection = el.connection;
    if (s.state === 'reconnecting') {
      connection.innerHTML = `<i class="bi bi-arrow-repeat"></i> S'ha tallat la connexió: es torna a connectar sol.`
        + (s.connectionError ? `<br>${escapeHtml(s.connectionError)}` : '');
    } else if (s.state === 'connecting' && s.connectionError) {
      connection.innerHTML = `<i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(s.connectionError)}<br>Es continua provant.`;
    } else {
      connection.innerHTML = '';
    }

    const server = this.#selectedServer(s);
    setEnabled(el.start, !active && !!s.deviceId && !!server);
    el.start.title = !s.deviceId ? 'Cal triar abans la font' : !server ? 'Cal afegir o triar abans un servidor' : 'Començar a emetre';
    setEnabled(el.stop, active);
    el.error.textContent = s.state === 'error' ? (s.error ?? '') : '';
    el.error.title = s.errorDetail ?? '';
  }

  /** Temps, bytes i reconnexions (stream.progress, un cop per segon). */
  renderProgress() {
    const s = this.#store.stream;
    if (!s) return;
    const el = this.#el;
    const p = s.progress ?? {};
    const active = ACTIVE.includes(s.state);
    el.elapsed.textContent = formatDuration(active ? (p.elapsed ?? 0) : 0);
    el.elapsed.classList.toggle('dim', s.state !== 'streaming');
    el.elapsed.classList.toggle('streaming', s.state === 'streaming');
    el.elapsed.title = 'Temps des que s\'ha premut «Inicia l\'emissió»';

    const server = this.#selectedServer(s);
    el.where.textContent = server ? `${server.name} · ${s.bitrateKbps} kbps` : 'Cap servidor';
    el.where.classList.toggle('dim', !server);

    const parts = [];
    if (active) {
      if (s.state === 'streaming') parts.push(`connectat fa ${formatDuration(p.connectedSeconds ?? 0)}`);
      parts.push(`${formatBytes(p.bytesSent ?? 0)} enviats`);
      if (p.reconnects > 0) parts.push(p.reconnects === 1 ? '1 reconnexió' : `${p.reconnects} reconnexions`);
      if (typeof p.levelerGainDb === 'number') parts.push(`Niv. ${signedDb(p.levelerGainDb)} dB`);
    }
    el.details.textContent = parts.join(' · ');
    el.details.title = el.details.textContent;

    // Dades que no s'han pogut enviar (la xarxa no ha donat l'abast o la connexió s'ha tallat): línia pròpia, en groc.
    const dropped = active && p.bytesDropped > 0;
    el.dropped.innerHTML = dropped
      ? `<i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(formatBytes(p.bytesDropped))} no enviats `
        + `(≈ ${escapeHtml(droppedTime(p.bytesDropped, s.bitrateKbps))} d'àudio)`
      : '';
    el.dropped.title = dropped
      ? 'So que no ha arribat al servidor perquè la xarxa no anava prou de pressa o la connexió s\'ha tallat. '
        + 'Si només passa en un tall, és normal; si va pujant sense talls, la connexió no dona l\'abast per a aquest bitrate.'
      : '';
  }

  /** Nivells nous (meters): [esquerre, dret] en dBFS i el que abaixen el DSP i el LIM. */
  updateMeter(/** @type {number[] | undefined} */ levels, /** @type {any} */ reduction) {
    if (levels) this.#meter.update(levels);
    if (reduction && !this.#el.gr.classList.contains('d-none')) {
      const value = Math.max(Number(reduction.processing ?? 0), Number(reduction.limiter ?? 0));
      this.#shownReduction = Math.max(value, this.#shownReduction - 0.4);
      this.#el['gr-fill'].style.width = `${Math.min(100, (this.#shownReduction / REDUCTION_SCALE_DB) * 100)}%`;
      this.#el['gr-value'].textContent = `${this.#shownReduction.toFixed(1).replace('.', ',')} dB`;
    }
  }

  // -------------------------------------------------------------------------

  /** @param {any} s */
  #selectedServer(s) {
    return s.servers?.find((/** @type {any} */ x) => x.id === s.serverId);
  }

  /** Desplegable de servidors: només es torna a fer si canvia la llista (si no, es tancaria mentre es fa servir). */
  #renderServers(/** @type {any} */ s, /** @type {boolean} */ active) {
    const select = /** @type {HTMLSelectElement} */ (this.#el.server);
    select.disabled = active;
    select.title = active ? 'Atura l\'emissió per canviar de servidor' : 'Servidor on s\'emetrà';
    const servers = /** @type {any[]} */ (s.servers ?? []);
    const key = JSON.stringify([s.serverId, servers.map((x) => [x.id, x.name])]);
    if (key === this.#serversKey) return;
    this.#serversKey = key;
    const options = servers.map((x) => `<option value="${escapeHtml(x.id)}"${x.id === s.serverId ? ' selected' : ''}>${escapeHtml(x.name)}</option>`);
    if (!s.serverId) options.unshift(`<option value="" selected disabled>${servers.length ? '— Tria un servidor —' : '— Cap servidor: afegeix-ne un —'}</option>`);
    select.innerHTML = options.join('');
  }

  /** NIV, DSP i LIM: encesos amb color i ✓ (color + marca + text), i la barra de reducció. */
  #renderProcessing(/** @type {any} */ s) {
    const el = this.#el;
    const target = String(this.#store.settings?.loudness?.targetLufs ?? -12).replace('-', '−');
    const level = !!s.leveler?.enabled;
    toggleButton(el.level, level, level
      ? `Anivellador ACTIVAT: iguala el volum de la parla cap a ${target} LUFS. Clica per desactivar-lo (de seguida; l'emissió avança 1 s).`
      : `Anivellador desactivat. Clica per igualar el volum de la parla cap a ${target} LUFS (de seguida; l'emissió s'endarrereix 1 s, els oients no ho noten).`);
    const dsp = s.processing ?? {};
    toggleButton(el.dsp, !!dsp.enabled, dsp.enabled
      ? `Compressor ACTIVAT (guany ${dsp.inputGainDb} dB, llindar ${dsp.thresholdDb} dB, ràtio ${dsp.ratio}:1). Clica per canviar-lo.`
      : 'Compressor desactivat. Clica per configurar-lo.');
    const lim = s.limiter ?? {};
    const ceiling = String((lim.ceilingDb ?? -1).toFixed(1)).replace('.', ',').replace('-', '−');
    toggleButton(el.lim, !!lim.enabled, lim.enabled
      ? `Limitador ACTIVAT: el que s'emet no passa de ${ceiling} dB. Clica per desactivar-lo (de seguida).`
      : `Limitador desactivat. Clica per activar-lo: el que s'emeti no passarà de ${ceiling} dB.`);
    el.gr.classList.toggle('d-none', !dsp.enabled && !lim.enabled);
  }

  /** Bitrate, freqüència i mono: només es poden canviar amb l'emissió aturada. */
  #renderQuality(/** @type {any} */ s, /** @type {boolean} */ active) {
    const el = this.#el;
    const bitrate = /** @type {HTMLSelectElement} */ (el.bitrate);
    const rate = /** @type {HTMLSelectElement} */ (el.rate);
    const mono = /** @type {HTMLInputElement} */ (el.mono);
    for (const control of [bitrate, rate, mono]) {
      control.disabled = active;
      control.title = active ? 'Atura l\'emissió per canviar la qualitat' : '';
    }
    const options = s.options ?? { bitrates: [128], sampleRates: [44100] };
    const key = JSON.stringify([s.bitrateKbps, s.sampleRate, options.bitrates, options.sampleRates]);
    if (key !== this.#qualityKey) {
      this.#qualityKey = key;
      bitrate.innerHTML = options.bitrates.map((/** @type {number} */ b) =>
        `<option value="${b}"${b === s.bitrateKbps ? ' selected' : ''}>${b} kbps</option>`).join('');
      rate.innerHTML = options.sampleRates.map((/** @type {number} */ r) =>
        `<option value="${r}"${r === s.sampleRate ? ' selected' : ''}>${(r / 1000).toLocaleString('ca-ES')} kHz</option>`).join('');
    }
    mono.checked = !!s.mono;
  }

  #sendTitle() {
    const title = /** @type {HTMLInputElement} */ (this.#el.title).value.trim();
    if (title === (this.#store.stream?.title ?? '')) return;
    this.#send('stream.setTitle', { title }).then((ok) => {
      if (ok && ACTIVE.includes(this.#store.stream?.state)) this.#showMessage('Títol enviat al servidor.');
    });
  }

  async #setDevice(/** @type {string} */ deviceId) {
    if (!(await this.#send('stream.setDevice', { deviceId }))) {
      this.#deviceSelect.invalidate();
      this.render();
    }
  }

  async #start() {
    // El títol que s'estigui escrivint també compta.
    this.#sendTitle();
    await this.#send('stream.start');
  }

  /** Aturar l'emissió interromp el que senten els oients: es demana confirmació (CLAUDE.md). */
  async #stop() {
    const server = this.#store.stream ? this.#selectedServer(this.#store.stream) : null;
    const choice = await choose({
      title: 'Aturar l\'emissió',
      icon: 'bi-broadcast',
      message: `Vols aturar l'emissió${server ? ` cap a «${server.name}»` : ''}?\nEls oients deixaran de sentir-vos.`,
      buttons: [
        { label: 'Cancel·lar', value: 'cancel' },
        { label: 'Aturar l\'emissió', value: 'stop', style: 'danger', icon: 'bi-stop-fill' },
      ],
    });
    if (choice === 'stop') this.#send('stream.stop');
  }

  /** Envia una ordre; si el motor la rebutja, en mostra el motiu uns segons. */
  async #send(/** @type {string} */ cmd, /** @type {object} */ params = {}) {
    try {
      const result = await this.#request(cmd, params);
      if (result?.stream) {
        this.#store.stream = result.stream;
        this.render();
      }
      return true;
    } catch (err) {
      this.#showMessage(/** @type {Error} */ (err).message);
      return false;
    }
  }

  #showMessage(/** @type {string} */ text) {
    this.#el.message.textContent = text;
    clearTimeout(this.#messageTimer);
    this.#messageTimer = window.setTimeout(() => { this.#el.message.textContent = ''; }, MESSAGE_MS);
  }
}

/** Botó NIV, DSP o LIM: encès (color + ✓) o apagat, amb la seva ajuda. */
function toggleButton(/** @type {HTMLElement} */ button, /** @type {boolean} */ on, /** @type {string} */ title) {
  button.classList.toggle('on', on);
  button.setAttribute('aria-pressed', String(on));
  button.title = title;
  /** @type {HTMLElement} */ (button.querySelector('i')).className = on ? 'bi bi-check-lg' : 'd-none';
}

/** Quant d'àudio són uns bytes d'MP3: «26 ms» o «1,5 s». */
function droppedTime(/** @type {number} */ bytes, /** @type {number} */ kbps) {
  const seconds = bytes * 8 / ((kbps || 128) * 1000);
  return seconds < 1 ? `${Math.max(1, Math.round(seconds * 1000))} ms` : `${seconds.toFixed(1).replace('.', ',')} s`;
}

/** «+3,5» o «−2,0». */
function signedDb(/** @type {number} */ db) {
  return `${db > 0 ? '+' : db < 0 ? '−' : ''}${Math.abs(db).toFixed(1).replace('.', ',')}`;
}

/** @param {HTMLElement} button @param {boolean} enabled */
function setEnabled(button, enabled) {
  /** @type {HTMLButtonElement} */ (button).disabled = !enabled;
}
