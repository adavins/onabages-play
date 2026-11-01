// Interfície principal d'Ona Bages Play — fase 0.
// Mostra el rellotge i l'estat de la connexió amb el motor.

import { EngineClient } from './engine-client.js';

const MAX_FAILED_RECONNECTS = 3;

const els = {
  clock: document.getElementById('clock'),
  status: document.getElementById('engine-status'),
  details: document.getElementById('engine-details'),
  retry: document.getElementById('btn-retry'),
};

const STATUS = {
  connecting: { cls: 'status-connecting', icon: 'bi-hourglass-split', text: 'Connectant amb el motor…' },
  connected: { cls: 'status-connected', icon: 'bi-check-circle-fill', text: 'Motor connectat' },
  error: { cls: 'status-error', icon: 'bi-exclamation-octagon-fill', text: 'Motor no disponible' },
};

/** @type {EngineClient | null} */
let client = null;
let failedReconnects = 0;

startClock();
setupClose();
els.retry.addEventListener('click', restartEngine);
init();

// ---------------------------------------------------------------------------

async function init() {
  if (!window.onabages) {
    setStatus('error', 'Obre la interfície des d\'Electron (npm start)');
    return;
  }

  setStatus('connecting');
  try {
    connectTo(await window.onabages.getEngineInfo());
  } catch (err) {
    setStatus('error', err.message);
  }
}

async function restartEngine() {
  setStatus('connecting', 'Arrencant el motor…');
  try {
    connectTo(await window.onabages.ensureEngine());
  } catch (err) {
    setStatus('error', err.message);
  }
}

function connectTo(info) {
  client?.close();
  failedReconnects = 0;

  client = new EngineClient(info.port, info.token);
  client.addEventListener('status', onConnectionStatus);
  client.addEventListener('ev:engine.status', (e) => showUptime(e.detail));
  client.connect();
}

async function onConnectionStatus(e) {
  const state = e.detail.state;

  if (state === 'connected') {
    failedReconnects = 0;
    setStatus('connected');
    try {
      const hello = await client.request('engine.hello');
      els.details.dataset.engine = `${hello.version} · protocol ${hello.protocol}`;
      els.details.textContent = els.details.dataset.engine;
    } catch (err) {
      console.warn('engine.hello ha fallat', err);
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

function showUptime(ev) {
  const engine = els.details.dataset.engine ?? '';
  els.details.textContent = `${engine} · en marxa ${formatDuration(ev.uptime)}`;
}

/**
 * Mostra l'estat del motor amb color, icona i text.
 * @param {'connecting'|'connected'|'error'} state
 * @param {string} [message] text alternatiu
 */
function setStatus(state, message) {
  const s = STATUS[state];
  els.status.className = `status ${s.cls}`;
  els.status.querySelector('i').className = `bi ${s.icon}`;
  els.status.querySelector('.label').textContent = message ?? s.text;
  els.status.title = message ? s.text : '';
  els.retry.classList.toggle('d-none', state !== 'error');
  if (state !== 'connected') els.details.textContent = '';
}

// ---------------------------------------------------------------------------

function startClock() {
  const tick = () => {
    els.clock.textContent = new Date().toLocaleTimeString('ca-ES', { hour12: false });
  };
  tick();
  setInterval(tick, 250);
}

function setupClose() {
  if (!window.onabages) return;

  window.onabages.onCloseRequested(async () => {
    // Fase 0: no hi ha reproduccions ni enregistraments, així que aturem el motor.
    // Fase 2: si n'hi ha d'actius, demanar confirmació i, si es tanca igualment,
    // deixar el motor en marxa.
    try {
      if (client?.connected) await client.request('engine.shutdown', {}, 1500);
    } catch {
      // Si no respon, tanquem igualment.
    }
    window.onabages.confirmClose();
  });
}

function formatDuration(totalSeconds) {
  const s = Math.floor(totalSeconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  return `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
}
