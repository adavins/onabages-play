// @ts-check
// Interfície principal d'Ona Bages Play.
// Connecta amb el motor, crea els panells i gestiona el zoom i el tancament.
// La interfície només mostra l'estat i envia ordres: tot l'àudio és al motor.

import { EngineClient } from './engine-client.js';
import { Store } from './store.js';
import { PlayerPanel } from './ui/player-panel.js';
import { RecorderPanel } from './ui/recorder-panel.js';
import { RecentsPanel } from './ui/recents-panel.js';
import { CuePanel } from './ui/cue-panel.js';
import { ExplorerTab } from './ui/explorer-tab.js';
import { editSettings } from './ui/settings-dialog.js';
import { setupShortcuts } from './ui/shortcuts.js';
import { choose } from './ui/dialogs.js';
import { setupProfileMenu } from './ui/profile-menu.js';
import { setupHelpMenu } from './ui/help-menu.js';
import { chooseStartupProfile } from './ui/startup-profile.js';
import { activeProfile, requirePin, markUnlocked, lockAll, minutesLeft, pinEvents } from './ui/pin-lock.js';
import { setupSidePanel } from './ui/side-panel.js';
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
  recents: $('recents'),
  cue: $('cue'),
  settings: $('btn-settings'),
  editor: $('btn-editor'),
  help: /** @type {HTMLButtonElement} */ ($('btn-help')),
  lock: /** @type {HTMLButtonElement} */ ($('btn-lock')),
  restartNotice: $('restart-notice'),
  programName: $('program-name'),
  profile: $('btn-profile'),
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
/** Ja s'ha mirat si cal fer triar el perfil en arrencar (pas 13b). */
let startupChecked = false;

/** @type {Map<string, PlayerPanel>} */
let playerPanels = new Map();
/** @type {Map<string, RecorderPanel>} */
let recorderPanels = new Map();
/** @type {RecentsPanel | null} */
let recentsPanel = null;
/** @type {CuePanel | null} */
let cuePanel = null;

startClock();
setupWindowControls();
setupClose();
setupZoom(saveZoom);
const sidePanel = setupSidePanel(store);
/** Últim zoom aplicat: si canvia amb el panell lateral obert, la finestra s'ha d'ajustar. */
let appliedZoom = 1;
setupPanels();
els.restart.addEventListener('click', restartEngine);
els.settings.addEventListener('click', openSettings);
els.editor.addEventListener('click', () => bridge?.openEditor());
setupHelpMenu(els.help);
setupLockButton();
setupProfileMenu({ button: els.profile, request, onSettings: applySettings, onSwitch: restartForProfile });
setupShortcuts(store, request, (playerId, text) => playerPanels.get(playerId)?.showMessage(text));
init();

// ---------------------------------------------------------------------------
// Capçalera com a barra de títol (D-060)
// ---------------------------------------------------------------------------

/**
 * La finestra no té barra de títol: la capçalera la substitueix (es pot arrossegar) i els botons de
 * Windows (minimitzar, maximitzar, tancar) es dibuixen a sobre, a la dreta. Cal:
 * - dir a Electron l'alçada de la capçalera, perquè els botons hi encaixin (canvia amb el zoom);
 * - deixar lloc a la dreta perquè els botons no tapin els de la capçalera (⚙, ✂...).
 */
function setupWindowControls() {
  const topbar = /** @type {HTMLElement} */ (document.querySelector('.topbar'));
  // Sense la vora de sota (clientHeight): si no, els botons de Windows la taparien.
  new ResizeObserver(() => bridge?.setTopbarHeight(topbar.clientHeight)).observe(topbar);

  /** @type {any} API del navegador per a aquest cas (Window Controls Overlay) */
  const overlay = /** @type {any} */ (navigator).windowControlsOverlay;
  const update = () => {
    // Espai que ocupen els botons de Windows, en píxels CSS. Si el navegador no ho diu, uns 140.
    const area = overlay?.visible ? overlay.getTitlebarAreaRect() : null;
    const width = area && area.width > 0 ? Math.max(0, window.innerWidth - (area.x + area.width)) : 140;
    document.documentElement.style.setProperty('--obp-window-controls', `${Math.ceil(width)}px`);
  };
  overlay?.addEventListener('geometrychange', update);
  window.addEventListener('resize', update);
  update();
}

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

/**
 * Canvi de perfil (D-058): el motor ja s'està aturant. Electron espera que s'aturi i n'arrenca un
 * de nou, que llegeix el perfil triat; aquí ens hi tornem a connectar i es tornen a muntar els panells.
 * @param {{ name: string }} profile
 */
async function restartForProfile(profile) {
  client?.close(); // sense els reintents normals: ara és normal que el motor no respongui
  setStatus('connecting', `Canviant al perfil «${profile.name}»…`);
  try {
    connectTo(await bridge.restartEngine());
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

      const { settings } = await client.request('settings.get');
      applySettings(settings);

      await store.refresh();
    } catch (err) {
      console.warn('No s\'ha pogut llegir l\'estat del motor', err);
    }
    // Triar el perfil (pas 13b): només la primera connexió, i només si el motor acaba d'arrencar.
    if (!startupChecked) {
      startupChecked = true;
      if (await bridge.takeStartupChoice()) {
        chooseStartupProfile({ request, busy: store.busy, onSwitch: restartForProfile });
      }
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
  // Per si de cas: en deixar anar a qualsevol lloc, cap panell no es queda amb l'avís d'arrossegar.
  window.addEventListener('drop', () => {
    document.querySelectorAll('.drop-target').forEach((el) => el.classList.remove('drop-target'));
  }, true);

  store.addEventListener('reset', buildPanels);
  store.addEventListener('player', (e) => {
    playerPanels.get(/** @type {any} */ (e).detail)?.render();
    cuePanel?.render(); // l'avís «surt per antena» depèn de la sortida dels reproductors
  });
  store.addEventListener('recorder', (e) => {
    recorderPanels.get(/** @type {any} */ (e).detail)?.render();
    recentsPanel?.render(); // el pendent d'un enregistrador no surt a «Recents»
  });
  store.addEventListener('recordings', () => {
    for (const panel of recorderPanels.values()) panel.render();
    recentsPanel?.render();
  });
  store.addEventListener('playlist', (e) => playerPanels.get(/** @type {any} */ (e).detail)?.renderPlaylist());
  // La preescolta: el seu panell i, a les llistes, la fila que hi sona (taronja).
  store.addEventListener('cue', renderAll);
  store.addEventListener('cuePosition', () => cuePanel?.render());
  store.addEventListener('devices', renderAll);
  store.addEventListener('meters', () => {
    for (const [id, panel] of playerPanels) panel.updateMeter(store.meters.players?.[id], store.meters.playerReduction?.[id]);
    for (const [id, panel] of recorderPanels) panel.updateMeter(store.meters.recorders?.[id], store.meters.recorderReduction?.[id]);
    cuePanel?.updateMeter(store.meters.cue);
  });
}

/** Crea un panell per a cada reproductor i enregistrador que té el motor. */
function buildPanels() {
  playerPanels = new Map([...store.players.keys()].map((id) => [id, new PlayerPanel(store, id, request)]));
  recorderPanels = new Map([...store.recorders.keys()].map((id) => [id, new RecorderPanel(store, id, request)]));
  recentsPanel = new RecentsPanel(store, request);
  cuePanel = new CuePanel(store, request);

  els.players.replaceChildren(...[...playerPanels.values()].map((p) => p.element));
  els.recorders.replaceChildren(...[...recorderPanels.values()].map((r) => r.element));
  els.recents.replaceWith(recentsPanel.element);
  els.recents = recentsPanel.element;
  els.cue.replaceWith(cuePanel.element);
  els.cue = cuePanel.element;

  // Explorador del panell lateral (pas 2c): les carpetes són del perfil, i es tornen a llegir en cada connexió.
  const audios = new ExplorerTab('audios', store, request);
  /** @type {HTMLElement} */ (document.querySelector('.side-tab[data-tab="audios"]')).replaceChildren(audios.element);
  const lists = new ExplorerTab('lists', store, request);
  /** @type {HTMLElement} */ (document.querySelector('.side-tab[data-tab="lists"]')).replaceChildren(lists.element);
}

function renderAll() {
  for (const panel of playerPanels.values()) panel.render();
  for (const panel of recorderPanels.values()) panel.render();
  recentsPanel?.render();
  cuePanel?.render();
}

// ---------------------------------------------------------------------------
// Zoom
// ---------------------------------------------------------------------------

/** @type {number | undefined} */
let zoomTimer;

/** @param {number} zoom */
async function saveZoom(zoom) {
  zoomChanged(zoom);
  els.zoom.textContent = `Zoom ${Math.round(zoom * 100)} %`;
  els.zoom.classList.add('visible');
  clearTimeout(zoomTimer);
  zoomTimer = window.setTimeout(() => els.zoom.classList.remove('visible'), 1500);

  try {
    const result = await client?.request('settings.set', { ui: { zoom } });
    if (result) store.settings = result.settings;
  } catch (err) {
    console.warn('No s\'ha pogut desar el zoom', err);
  }
}

/** Amb el panell lateral obert, un altre zoom vol dir una altra amplada en píxels de pantalla. @param {number} zoom */
function zoomChanged(zoom) {
  if (zoom === appliedZoom) return;
  appliedZoom = zoom;
  sidePanel.refreshSize();
}

// ---------------------------------------------------------------------------
// Configuració (pas 9, D-041)
// ---------------------------------------------------------------------------

/**
 * Aplica els ajustos del motor: escala, enregistradors per fila, nom del programa i avís de reiniciar.
 * (L'avís de final de pista el llegeix cada reproductor de store.settings.)
 * @param {any} settings
 */
function applySettings(settings) {
  store.settings = settings;
  applyZoom(settings.ui.zoom);
  zoomChanged(settings.ui.zoom);
  // Perfil actiu (fase 4). El programa només es mostra a part si es diu diferent que el perfil.
  const profileName = settings.profile?.name ?? '';
  /** @type {HTMLElement} */ (els.profile.querySelector('.profile-label')).textContent = profileName || 'Perfil';
  const programName = settings.program?.name ?? '';
  els.programName.textContent = programName;
  els.programName.classList.toggle('d-none', !programName || programName.toLowerCase() === profileName.toLowerCase());
  document.documentElement.style.setProperty('--obp-recorders-per-row', String(settings.ui.recordersPerRow));
  els.restartNotice.classList.toggle('d-none', !settings.restartNeeded);
  els.restartNotice.title = settings.restartNeeded
    ? `Has canviat el nombre de reproductors o d'enregistradors (${settings.layout.players} i ${settings.layout.recorders}).\n` +
      'S\'aplicarà quan tanquis l\'aplicació (amb el motor aturat) i la tornis a obrir.'
    : '';
  renderAll();
}

/** Botó ⚙: finestra de configuració. */
async function openSettings() {
  if (!client?.connected || !store.settings) return;
  // PIN del perfil actiu (pas 13c), si en té i no està desbloquejat.
  let profile;
  try {
    profile = await activeProfile(request);
  } catch {
    return;
  }
  if (!profile || !(await requirePin(request, profile, 'obrir la configuració'))) return;
  const pin = {
    profileName: profile.name,
    hasPin: !!profile.hasPin,
    /** @param {string | null} value */
    set: async (value) => {
      await request('profile.setPin', { profile: profile.id, pin: value });
      if (value !== null) markUnlocked(profile.id); // qui l'acaba de posar no l'ha de tornar a escriure
    },
  };
  const changes = await editSettings(store.settings, { players: store.players.size, recorders: store.recorders.size }, pin);
  if (!changes) return;
  try {
    const { settings } = await client.request('settings.set', changes);
    applySettings(settings);
  } catch (err) {
    await choose({
      title: 'Configuració',
      icon: 'bi-exclamation-triangle-fill',
      message: `No s'ha pogut desar la configuració:\n${/** @type {Error} */ (err).message}`,
      buttons: [{ label: 'D\'acord', value: 'ok', style: 'primary' }],
    });
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
// Cadenat del PIN (pas 13c): visible mentre algun perfil està desbloquejat; un clic el torna a bloquejar.
// ---------------------------------------------------------------------------

function setupLockButton() {
  const update = () => {
    const minutes = minutesLeft();
    els.lock.classList.toggle('d-none', minutes === 0);
    /** @type {HTMLElement} */ (els.lock.querySelector('.js-lock-label')).textContent = ` ${minutes} min`;
    els.lock.title = `PIN entrat: no es tornarà a demanar durant ${minutes} min. Clica per demanar-lo de nou ara.`;
  };
  els.lock.addEventListener('click', () => lockAll());
  pinEvents.addEventListener('change', update);
  setInterval(update, 30_000); // els minuts que queden, al tooltip
  update();
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

  // L'editor es tanca amb la finestra principal: si té canvis sense desar, primer es confirma.
  try {
    const { editor } = await client.request('editor.get', {}, 1500);
    if (editor.modified) {
      const name = editor.path.split(/[\\/]/).pop();
      const choice = await choose({
        title: 'Tancar Ona Bages Play',
        icon: 'bi-exclamation-triangle-fill',
        message: `L'editor té canvis sense desar a «${name}».\nSi tanques, es perdran (el fitxer original no es toca).`,
        buttons: [
          { label: 'Cancel·lar', value: 'cancel' },
          { label: 'Tancar sense desar', value: 'close', style: 'danger' },
        ],
      });
      if (choice !== 'close') return;
    }
  } catch {
    // Sense resposta de l'editor: es continua com sempre.
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
