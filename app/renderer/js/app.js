// @ts-check
// Interfície principal d'Ona Bages Play.
// Connecta amb el motor, crea els panells i gestiona el zoom i el tancament.
// La interfície només mostra l'estat i envia ordres: tot l'àudio és al motor.

import { EngineClient } from './engine-client.js';
import { Store } from './store.js';
import { PlayerPanel } from './ui/player-panel.js';
import { RecorderPanel } from './ui/recorder-panel.js';
import { choose } from './ui/dialogs.js';
import { setupZoom, applyZoom } from './ui/zoom.js';
import { formatDuration } from './ui/format.js';

/** Reintents de connexió abans de donar el motor per caigut. */
const MAX_FAILED_RECONNECTS = 3;

/** @type {any} API de preload.js (només existeix dins d'Electron) */
const bridge = /** @type {any} */ (window).onabages;

const $ = (/** @type {string} */ id) => /** @type {HTMLElement} */ (document.getElementById(id));
const els = {
  clock: $('clock'),
  status: $('engine-status'),
  details: $('engine-details'),
  zoom: $('zoom-indicator'),
  down: $('engine-down'),
  downDetail: $('engine-down-detail'),
  restart: $('btn-restart-engine'),
  players: $('players'),
  recorders: $('recorders'),
};

const STATUS = {
  connecting: { cls: 'status-connecting', icon: 'bi-hourglass-split', text: 'Connectant amb el motor…' },
  connected: { cls: 'status-connected', icon: 'bi-check-circle-fill', text: 'Motor connectat' },
  error: { cls: 'status-error', icon: 'bi-exclamation-octagon-fill', text: 'Motor no disponible' },
};

const store = new Store();

/** @type {EngineClient | null} */
let client = null;
let failedReconnects = 0;
let engineVersion = '';

/** @type {Map<string, PlayerPanel>} */
let playerPanels = new Map();
/** @type {Map<string, RecorderPanel>} */
let recorderPanels = new Map();

startClock();
setupClose();
setupZoom(saveZoom);
setupPanels();
els.restart.addEventListener('click', restartEngine);
init();

// ---------------------------------------------------------------------------
// Connexió amb el motor
// ---------------------------------------------------------------------------

async function init() {
  if (!bridge) {
    setStatus('error', 'Obre la interfície des d\'Electron (npm start)');
    return;
  }

  setStatus('connecting');
  try {
    connectTo(await bridge.getEngineInfo());
  } catch (err) {
    setStatus('error', /** @type {Error} */ (err).message);
  }
}

async function restartEngine() {
  setStatus('connecting', 'Arrencant el motor…');
  try {
    connectTo(await bridge.ensureEngine());
  } catch (err) {
    setStatus('error', /** @type {Error} */ (err).message);
  }
}

/** @param {{ port: number, token: string }} info */
function connectTo(info) {
  client?.close();
  failedReconnects = 0;

  client = new EngineClient(info.port, info.token);
  store.attach(client);
  client.addEventListener('status', onConnectionStatus);
  client.addEventListener('ev:engine.status', (e) => showUptime(/** @type {any} */ (e).detail));
  client.connect();
}

/** @param {Event} e */
async function onConnectionStatus(e) {
  const state = /** @type {any} */ (e).detail.state;
  if (!client) return;

  if (state === 'connected') {
    failedReconnects = 0;
    setStatus('connected');
    try {
      const hello = await client.request('engine.hello');
      engineVersion = `${hello.version} · protocol ${hello.protocol}`;
      els.details.textContent = engineVersion;

      const settings = await client.request('settings.get');
      applyZoom(settings.settings.ui.zoom);

      await store.refresh();
    } catch (err) {
      console.warn('No s\'ha pogut llegir l\'estat del motor', err);
    }
    return;
  }

  if (state === 'disconnected') {
    failedReconnects++;
    if (failedReconnects >= MAX_FAILED_RECONNECTS) {
      client.close(); // deixem de reintentar; l'operador decideix
      setStatus('error', 'El motor no respon');
    } else {
      setStatus('connecting', 'Connexió perduda, reintentant…');
    }
  }
}

/** @param {{ uptime: number }} ev */
function showUptime(ev) {
  els.details.textContent = `${engineVersion} · en marxa ${formatDuration(ev.uptime)}`;
}

/**
 * Mostra l'estat del motor amb color, icona i text.
 * @param {'connecting'|'connected'|'error'} state
 * @param {string} [message] text alternatiu
 */
function setStatus(state, message) {
  const s = STATUS[state];
  els.status.className = `status ${s.cls}`;
  /** @type {HTMLElement} */ (els.status.querySelector('i')).className = `bi ${s.icon}`;
  /** @type {HTMLElement} */ (els.status.querySelector('.label')).textContent = message ?? s.text;
  els.status.title = message ? s.text : '';
  if (state !== 'connected') els.details.textContent = '';

  // Sense motor, l'estat dels panells és antic: es mostren apagats.
  document.body.classList.toggle('engine-offline', state !== 'connected');

  // Avís molt visible quan el motor no respon.
  els.down.classList.toggle('d-none', state !== 'error');
  if (state === 'error') els.downDetail.textContent = `${message ?? ''}. El so i els enregistraments s'han aturat.`;
}

// ---------------------------------------------------------------------------
// Panells
// ---------------------------------------------------------------------------

/**
 * Envia una ordre al motor (els panells la fan servir per als botons).
 * @param {string} cmd
 * @param {object} [params]
 */
function request(cmd, params = {}) {
  if (!client?.connected) return Promise.reject(new Error('El motor no està connectat.'));
  return client.request(cmd, params);
}

function setupPanels() {
  // Un fitxer deixat anar fora d'un panell no ha de fer res (Electron l'obriria com una pàgina).
  window.addEventListener('dragover', (e) => e.preventDefault());
  window.addEventListener('drop', (e) => e.preventDefault());

  store.addEventListener('reset', buildPanels);
  store.addEventListener('player', (e) => playerPanels.get(/** @type {any} */ (e).detail)?.render());
  store.addEventListener('recorder', (e) => recorderPanels.get(/** @type {any} */ (e).detail)?.render());
  store.addEventListener('playlist', (e) => playerPanels.get(/** @type {any} */ (e).detail)?.renderPlaylist());
  store.addEventListener('devices', renderAll);
  store.addEventListener('meters', () => {
    for (const [id, panel] of playerPanels) panel.updateMeter(store.meters.players?.[id]);
    for (const [id, panel] of recorderPanels) panel.updateMeter(store.meters.recorders?.[id]);
  });
}

/** Crea un panell per a cada reproductor i enregistrador que té el motor. */
function buildPanels() {
  playerPanels = new Map([...store.players.keys()].map((id) => [id, new PlayerPanel(store, id, request)]));
  recorderPanels = new Map([...store.recorders.keys()].map((id) => [id, new RecorderPanel(store, id)]));

  els.players.replaceChildren(...[...playerPanels.values()].map((p) => p.element));
  els.recorders.replaceChildren(...[...recorderPanels.values()].map((r) => r.element));
}

function renderAll() {
  for (const panel of playerPanels.values()) panel.render();
  for (const panel of recorderPanels.values()) panel.render();
}

// ---------------------------------------------------------------------------
// Zoom
// ---------------------------------------------------------------------------

/** @type {number | undefined} */
let zoomTimer;

/** @param {number} zoom */
async function saveZoom(zoom) {
  els.zoom.textContent = `Zoom ${Math.round(zoom * 100)} %`;
  els.zoom.classList.add('visible');
  clearTimeout(zoomTimer);
  zoomTimer = window.setTimeout(() => els.zoom.classList.remove('visible'), 1500);

  try {
    await client?.request('settings.set', { ui: { zoom } });
  } catch (err) {
    console.warn('No s\'ha pogut desar el zoom', err);
  }
}

// ---------------------------------------------------------------------------
// Rellotge
// ---------------------------------------------------------------------------

function startClock() {
  const tick = () => {
    els.clock.textContent = new Date().toLocaleTimeString('ca-ES', { hour12: false });
  };
  tick();
  setInterval(tick, 250);
}

// ---------------------------------------------------------------------------
// Tancament (especificació 4.5)
// ---------------------------------------------------------------------------

function setupClose() {
  if (!bridge) return;
  bridge.onCloseRequested(onCloseRequested);
}

async function onCloseRequested() {
  // Sense motor no hi ha res a protegir.
  if (!client?.connected) {
    bridge.confirmClose();
    return;
  }

  const { playing, recording } = store.busy;
  if (playing.length === 0 && recording.length === 0) {
    // No hi ha res actiu: s'atura el motor i es tanca.
    try {
      await client.request('engine.shutdown', {}, 1500);
    } catch {
      // Si no respon, es tanca igualment. (Si s'ha posat a enregistrar ara mateix,
      // el motor ho rebutja i continua: no es perd res.)
    }
    bridge.confirmClose();
    return;
  }

  const lines = [];
  if (recording.length > 0) lines.push(`Enregistrant: ${recording.map((id) => `Enregistrador ${id}`).join(', ')}`);
  if (playing.length > 0) lines.push(`Sonant: ${playing.map((id) => `Reproductor ${id}`).join(', ')}`);

  const choice = await choose({
    title: 'Tancar Ona Bages Play',
    icon: 'bi-exclamation-triangle-fill',
    message: `${lines.join('\n')}\n\nSi tanques només la finestra, el so i els enregistraments continuen. En tornar a obrir l'aplicació, s'hi tornarà a connectar.`,
    buttons: [
      { label: 'Cancel·lar', value: 'cancel' },
      { label: 'Tancar només la finestra', value: 'window', style: 'primary', icon: 'bi-window-x' },
      { label: 'Aturar-ho tot i tancar', value: 'stop', style: 'danger', icon: 'bi-stop-circle' },
    ],
  });

  if (choice === 'window') {
    bridge.confirmClose();
    return;
  }

  if (choice === 'stop') {
    const sure = await choose({
      title: 'Aturar-ho tot',
      icon: 'bi-exclamation-octagon-fill',
      message: 'S\'aturarà el so i es tancaran els enregistraments (els fitxers queden desats).\n\nN\'estàs segur?',
      buttons: [
        { label: 'No', value: 'no' },
        { label: 'Sí, aturar-ho tot', value: 'yes', style: 'danger' },
      ],
    });
    if (sure !== 'yes') return;

    try {
      await client.request('engine.shutdown', { force: true }, 5000);
    } catch (err) {
      console.warn('engine.shutdown ha fallat', err);
    }
    bridge.confirmClose();
  }
}
