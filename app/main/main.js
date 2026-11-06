'use strict';

// Procés principal d'Electron.
// Responsabilitats: obrir la finestra, assegurar que el motor està en marxa
// i passar-ne la informació de connexió (port i testimoni) a la interfície.
// Aquí NO hi ha àudio: tot l'àudio és al motor .NET.

const { app, BrowserWindow, dialog, ipcMain, screen, shell } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');

const DEFAULT_PORT = 47800;
const ENGINE_START_TIMEOUT_MS = 60_000; // "dotnet run" pot trigar la primera vegada
const REPO_ROOT = path.resolve(__dirname, '..', '..');

// Carpeta de dades (D-044). Versió portable: si al costat de l'executable hi ha una carpeta
// "dades", tot es desa allà (també la memòria cau d'Electron). Si no, a %APPDATA%\OnaBages.
// Per provar el mode portable sense empaquetar: variable d'entorn ONABAGES_DATA_DIR.
const PORTABLE_DATA_DIR = path.join(path.dirname(process.execPath), 'dades');
const DATA_DIR = process.env.ONABAGES_DATA_DIR
  || (app.isPackaged && fs.existsSync(PORTABLE_DATA_DIR) ? PORTABLE_DATA_DIR : path.join(app.getPath('appData'), 'OnaBages'));
const IS_PORTABLE = DATA_DIR !== path.join(app.getPath('appData'), 'OnaBages');
if (IS_PORTABLE) app.setPath('userData', path.join(DATA_DIR, 'electron')); // abans de res més
const RUNTIME_FILE = path.join(DATA_DIR, 'engine.json');

/** Icona de les finestres (la de l'executable la posa empaquetar.cmd). Es genera amb tools/icona/generar-icona.js. */
const APP_ICON = path.join(__dirname, '..', 'assets', 'icona.ico');

/** @type {BrowserWindow | null} */
let mainWindow = null;
/** @type {{ port: number, token: string } | null} */
let engineInfo = null;
/** La primera comprovació del motor d'aquesta sessió encara no s'ha fet. */
let firstEngineCheck = true;
/** El motor s'ha arrencat en obrir l'aplicació i la interfície encara no ho ha preguntat (pas 13b). */
let startupChoicePending = false;
let closeConfirmed = false;

// Només una finestra de l'aplicació alhora.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });
  app.whenReady().then(createWindow);
}

app.on('window-all-closed', () => app.quit());

function createWindow() {
  // On era la finestra l'última vegada (o la mida per defecte).
  const { bounds, maximized } = windowBounds('main', { width: 1600, height: 900, minWidth: 512, minHeight: 340 });
  mainWindow = new BrowserWindow({
    ...bounds,
    show: false,
    title: 'Ona Bages Play',
    backgroundColor: '#121417',
    icon: APP_ICON,
    autoHideMenuBar: true,
    // Sense la barra de títol de Windows (D-060): la capçalera de l'aplicació en fa la feina i els
    // botons de la finestra (minimitzar, maximitzar, tancar) es dibuixen a sobre, a la dreta.
    titleBarStyle: 'hidden',
    titleBarOverlay: { ...TITLE_BAR_COLORS, height: titleBarHeight() },
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  if (maximized) mainWindow.maximize();
  mainWindow.show();
  mainWindow.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'));

  // Sense el menú per defecte d'Electron: les seves dreceres (zoom, recàrrega...) passarien
  // per davant de les de l'aplicació. El zoom el gestiona la interfície (js/ui/zoom.js).
  mainWindow.removeMenu();

  // F12 obre les eines de desenvolupament (només per desenvolupar; no a l'aplicació instal·lada).
  mainWindow.webContents.on('before-input-event', (event, input) => {
    if (input.type === 'keyDown' && input.key === 'F12' && !app.isPackaged) {
      mainWindow?.webContents.toggleDevTools();
      event.preventDefault();
    }
  });

  // En tancar, primer avisem la interfície: si hi ha so o enregistraments, demana què cal fer.
  // No hi ha cap temporitzador: la interfície pot estar esperant la resposta de l'operador.
  mainWindow.on('close', (event) => {
    if (closeConfirmed) {
      if (mainWindow) saveWindowState('main', mainWindow);
      return;
    }
    event.preventDefault();
    mainWindow?.webContents.send('app:close-requested');
  });

  // Si la interfície ha caigut, ja no pot respondre: s'ha de poder tancar igualment.
  // (El motor és un procés a part i continua: no es perd cap enregistrament.)
  mainWindow.webContents.on('render-process-gone', () => {
    closeConfirmed = true;
  });

  mainWindow.on('closed', () => {
    mainWindow = null;
    // L'editor no té sentit sense la finestra principal (la principal ja ha avisat dels canvis sense desar).
    if (editorWindow) saveWindowState('editor', editorWindow);
    editorWindow?.destroy();
  });

  mainWindow.webContents.once('did-finish-load', offerDesktopShortcut);
}

/**
 * Versió portable: la primera vegada que s'obre des d'una carpeta, pregunta si vol una drecera
 * a l'escriptori (o actualitzar-la, si apunta a una altra carpeta). Només una vegada per carpeta.
 */
async function offerDesktopShortcut() {
  if (!app.isPackaged || !IS_PORTABLE || !mainWindow) return;

  const shortcut = path.join(app.getPath('desktop'), 'Ona Bages Play.lnk');
  const askedFile = path.join(app.getPath('userData'), 'drecera-preguntada.txt');
  try {
    if (fs.readFileSync(askedFile, 'utf8') === process.execPath) return; // ja s'ha preguntat des d'aquesta carpeta
  } catch { /* encara no s'ha preguntat mai */ }

  let current = null;
  try { current = shell.readShortcutLink(shortcut).target; } catch { /* no n'hi ha */ }
  if (current && path.resolve(current) === path.resolve(process.execPath)) return; // ja és correcta

  const { response } = await dialog.showMessageBox(mainWindow, {
    type: 'question',
    title: 'Ona Bages Play',
    message: current ? 'La drecera de l\'escriptori apunta a una altra carpeta. Vols que obri aquesta?' : 'Vols una drecera a l\'escriptori?',
    detail: `Aplicació: ${path.dirname(process.execPath)}`,
    buttons: [current ? 'Actualitzar la drecera' : 'Crear la drecera', 'No, gràcies'],
    defaultId: 0,
    cancelId: 1,
  });

  try { fs.writeFileSync(askedFile, process.execPath, 'utf8'); } catch { /* es tornarà a preguntar */ }
  if (response !== 0) return;

  const ok = shell.writeShortcutLink(shortcut, current ? 'replace' : 'create', {
    target: process.execPath,
    cwd: path.dirname(process.execPath),
    description: 'Ona Bages Play',
  });
  if (!ok) {
    await dialog.showMessageBox(mainWindow, {
      type: 'warning',
      title: 'Ona Bages Play',
      message: 'No s\'ha pogut crear la drecera.',
      detail: 'La pots crear a mà: botó dret a sobre d\'OnaBagesPlay.exe → Enviar a → Escriptori.',
    });
  }
}

// ---------------------------------------------------------------------------
// Motor
// ---------------------------------------------------------------------------

/** Llegeix el fitxer d'execució que escriu el motor quan arrenca. */
function readRuntimeFile() {
  try {
    const data = JSON.parse(fs.readFileSync(RUNTIME_FILE, 'utf8'));
    if (data && typeof data.port === 'number' && typeof data.token === 'string') {
      return { port: data.port, token: data.token };
    }
  } catch {
    // No existeix o no es pot llegir: el motor no està en marxa.
  }
  return null;
}

/** Comprova si hi ha un motor responent en aquest port. */
async function isEngineAlive(port) {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/health`, { signal: AbortSignal.timeout(1000) });
    return res.ok;
  } catch {
    return false;
  }
}

/** Decideix com s'arrenca el motor: executable compilat o, si no n'hi ha, "dotnet run". */
function engineCommand(port, token) {
  const engineArgs = ['--port', String(port), '--token', token, '--data-dir', DATA_DIR];

  if (process.env.ONABAGES_ENGINE) {
    return { command: process.env.ONABAGES_ENGINE, args: engineArgs };
  }

  const exeName = process.platform === 'win32' ? 'OnaBages.Engine.exe' : 'OnaBages.Engine';
  const candidates = [
    // Aplicació empaquetada (empaquetar.cmd hi copia el motor publicat).
    path.join(process.resourcesPath || '', 'engine', exeName),
    // Desenvolupament: després de "dotnet build".
    path.join(REPO_ROOT, 'engine', 'OnaBages.Engine', 'bin', 'Debug', 'net10.0-windows', exeName),
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return { command: candidate, args: engineArgs };
  }

  return {
    command: 'dotnet',
    args: ['run', '--project', path.join(REPO_ROOT, 'engine', 'OnaBages.Engine'), '--', ...engineArgs],
  };
}

/**
 * Retorna la informació d'un motor en marxa. Si no n'hi ha cap, l'arrenca.
 * El motor s'arrenca com a procés independent: continua funcionant encara que
 * la interfície es tanqui de manera inesperada.
 */
async function ensureEngine(reuseToken = null) {
  const first = firstEngineCheck;
  firstEngineCheck = false;

  const existing = readRuntimeFile();
  if (existing && (await isEngineAlive(existing.port))) {
    engineInfo = existing;
    return engineInfo;
  }

  // El motor arrenca ara, en obrir l'aplicació: la interfície pot fer triar el perfil (fase 5, pas 13b).
  if (first) startupChoicePending = true;

  const port = DEFAULT_PORT;
  const token = reuseToken ?? crypto.randomBytes(24).toString('hex');
  const { command, args } = engineCommand(port, token);

  let spawnError = null;
  const child = spawn(command, args, {
    cwd: app.isPackaged ? path.dirname(command) : REPO_ROOT,
    detached: true,
    stdio: 'ignore',
    windowsHide: true,
  });
  child.on('error', (err) => {
    spawnError = err;
  });
  child.unref();

  const deadline = Date.now() + ENGINE_START_TIMEOUT_MS;
  while (Date.now() < deadline) {
    if (spawnError) {
      throw new Error(`No s'ha pogut arrencar el motor (${command}): ${spawnError.message}`);
    }
    if (await isEngineAlive(port)) {
      engineInfo = { port, token };
      return engineInfo;
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }

  throw new Error(`El motor no ha respost en ${ENGINE_START_TIMEOUT_MS / 1000} s. Prova d'executar "dotnet build" a la carpeta engine.`);
}

// ---------------------------------------------------------------------------
// Comunicació amb la interfície (via preload.js)
// ---------------------------------------------------------------------------

ipcMain.handle('engine:get-info', async () => engineInfo ?? ensureEngine());

/**
 * Finestra inicial per triar el perfil (fase 5, pas 13b): només si el motor s'ha arrencat en obrir
 * l'aplicació (si ja era en marxa, potser sona o enregistra). Es respon true una sola vegada:
 * si la finestra es torna a carregar, no torna a sortir.
 */
ipcMain.handle('app:take-startup-choice', () => {
  const pending = startupChoicePending;
  startupChoicePending = false;
  return pending;
});

ipcMain.handle('engine:ensure', async () => {
  engineInfo = null;
  return ensureEngine();
});

/**
 * Canvi de perfil (D-058): el motor s'ha aturat sol després de profile.switch. S'espera que s'acabi
 * d'aturar i se n'arrenca un de nou, que llegirà el perfil triat. Amb el mateix port i testimoni,
 * perquè les altres finestres (l'editor) s'hi tornin a connectar soles.
 */
ipcMain.handle('engine:restart', async () => {
  const previous = engineInfo ?? readRuntimeFile();
  engineInfo = null;
  if (previous) {
    const deadline = Date.now() + 20_000;
    while (await isEngineAlive(previous.port)) {
      if (Date.now() > deadline) throw new Error('El motor no s\'ha aturat en 20 s.');
      await new Promise((resolve) => setTimeout(resolve, 300));
    }
  }
  return ensureEngine(previous?.token ?? null);
});

/** Formats que el motor pot llegir (especificació 6.3). */
const AUDIO_EXTENSIONS = ['mp3', 'wav', 'm4a', 'aac', 'flac', 'wma', 'ogg'];
const PLAYLIST_EXTENSIONS = ['obpl', 'm3u', 'm3u8'];
let lastAudioFolder = undefined;

/** Diàleg per triar fitxers d'àudio (un o diversos). Recorda l'última carpeta mentre l'aplicació és oberta. */
ipcMain.handle('dialog:open-audio', async () => {
  if (!mainWindow) return [];
  const result = await dialog.showOpenDialog(mainWindow, {
    title: 'Afegir pistes a la llista',
    defaultPath: lastAudioFolder,
    properties: ['openFile', 'multiSelections'],
    filters: [
      { name: 'Àudio', extensions: AUDIO_EXTENSIONS },
      { name: 'Llistes (s\'hi afegeixen les seves pistes)', extensions: PLAYLIST_EXTENSIONS },
      { name: 'Tots els fitxers', extensions: ['*'] },
    ],
  });
  if (result.canceled || result.filePaths.length === 0) return [];
  lastAudioFolder = path.dirname(result.filePaths[0]);
  return result.filePaths;
});

/** Zoom d'Electron (de 0,7 a 1,5). Per sota de 0,7 la interfície no redueix la lletra: només els espais i els elements grans (js/ui/zoom.js). */
ipcMain.handle('ui:set-zoom', (_event, factor) => {
  if (typeof factor === 'number' && factor >= 0.7 && factor <= 1.5) {
    mainWindow?.webContents.setZoomFactor(factor);
    updateTitleBarOverlay(); // la capçalera ha canviat d'alçada en píxels de pantalla
  }
});

// ---------------------------------------------------------------------------
// Panell lateral (D-062): en obrir-lo, la finestra es fa més ampla
// ---------------------------------------------------------------------------

/**
 * Quant ha crescut la finestra per obrir el panell lateral (píxels de pantalla) i quant s'ha
 * desplaçat cap a l'esquerra (si a la dreta no hi cabia). null = no ha crescut.
 * @type {{ extra: number, movedLeft: number } | null}
 */
let sidePanelGrowth = null;

/**
 * La interfície obre o tanca el panell lateral. En obrir-lo, la finestra creix l'amplada del panell
 * (cap a la dreta o, si no hi cap, cap a l'esquerra), perquè els reproductors no canviïn de mida.
 * Retorna 'grow' si ha crescut, o 'overlay' si no podia (maximitzada o sense lloc a la pantalla):
 * llavors el panell surt per sobre dels reproductors.
 */
ipcMain.handle('ui:side-panel', (_event, open, cssWidth) => {
  if (!mainWindow || mainWindow.isDestroyed()) return 'overlay';

  if (!open) {
    shrinkFromSidePanel();
    return 'closed';
  }

  if (sidePanelGrowth) return 'grow'; // ja havia crescut
  if (typeof cssWidth !== 'number' || !(cssWidth > 0 && cssWidth < 2000)) return 'overlay';
  if (mainWindow.isMaximized() || mainWindow.isFullScreen()) return 'overlay';

  const extra = Math.round(cssWidth * mainWindow.webContents.getZoomFactor());
  const bounds = mainWindow.getBounds();
  const area = screen.getDisplayMatching(bounds).workArea;
  const width = bounds.width + extra;
  if (width > area.width) return 'overlay';

  // Cap a la dreta; si no hi cap, la finestra es desplaça a l'esquerra el que calgui.
  const x = Math.min(bounds.x, area.x + area.width - width);
  mainWindow.setBounds({ x, y: bounds.y, width, height: bounds.height });
  sidePanelGrowth = { extra, movedLeft: bounds.x - x };
  return 'grow';
});

/** Torna la finestra a la mida (i el lloc) d'abans d'obrir el panell. */
function shrinkFromSidePanel() {
  const growth = sidePanelGrowth;
  sidePanelGrowth = null;
  if (!growth || !mainWindow || mainWindow.isDestroyed()) return;
  // Si mentrestant s'ha maximitzat, ja no es toca (en restaurar-la, Windows li tornarà la mida que tenia).
  if (mainWindow.isMaximized() || mainWindow.isFullScreen()) return;
  const bounds = mainWindow.getBounds();
  mainWindow.setBounds({
    x: bounds.x + growth.movedLeft,
    y: bounds.y,
    width: Math.max(512, bounds.width - growth.extra),
    height: bounds.height,
  });
}

/** On és la finestra principal sense el panell lateral (el que es desa en tancar). */
function mainBoundsWithoutSidePanel() {
  const bounds = mainWindow.getNormalBounds();
  if (!sidePanelGrowth || mainWindow.isMaximized()) return bounds;
  return {
    ...bounds,
    x: bounds.x + sidePanelGrowth.movedLeft,
    width: Math.max(512, bounds.width - sidePanelGrowth.extra),
  };
}

// ---------------------------------------------------------------------------
// Capçalera com a barra de títol (D-060)
// ---------------------------------------------------------------------------

/** Colors dels botons de la finestra: el fons de la capçalera (--bs-tertiary-bg del tema fosc) i el text. */
const TITLE_BAR_COLORS = { color: '#2b3035', symbolColor: '#dee2e6' };

/** Alçada de la capçalera en píxels CSS, sense la vora de sota (la diu la interfície; de moment, una aproximació). */
let topbarCssHeight = 47;

/** Alçada dels botons de la finestra: la de la capçalera, en píxels de pantalla (amb el zoom). */
function titleBarHeight() {
  const zoom = mainWindow?.webContents.getZoomFactor() ?? 1;
  // Arrodonit avall: mai més alts que la capçalera, perquè no tapin la vora de sota.
  return Math.max(24, Math.floor(topbarCssHeight * zoom));
}

function updateTitleBarOverlay() {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.setTitleBarOverlay({ ...TITLE_BAR_COLORS, height: titleBarHeight() });
}

/** La interfície diu l'alçada de la capçalera (canvia amb el zoom i amb l'escala de la interfície). */
ipcMain.on('ui:topbar-height', (_event, cssHeight) => {
  if (typeof cssHeight !== 'number' || !(cssHeight > 0 && cssHeight < 400)) return;
  topbarCssHeight = cssHeight;
  updateTitleBarOverlay();
});

ipcMain.on('app:close-confirmed', () => {
  closeConfirmed = true;
  mainWindow?.close();
});

// ---------------------------------------------------------------------------
// Posició i mida de les finestres (D-055)
// ---------------------------------------------------------------------------
// Es desen a "finestres.json", a la carpeta d'Electron (dades\electron en la versió portable).
// No és configuració de l'aplicació (que és al motor): és només on era cada finestra.
// Si el fitxer es perd o està malmès, les finestres surten a la mida per defecte.

function windowStatesFile() {
  return path.join(app.getPath('userData'), 'finestres.json');
}

/** @returns {Record<string, { x: number, y: number, width: number, height: number, maximized: boolean }>} */
function readWindowStates() {
  try {
    const data = JSON.parse(fs.readFileSync(windowStatesFile(), 'utf8'));
    if (data && data.schemaVersion === 1 && data.windows && typeof data.windows === 'object') return data.windows;
  } catch { /* encara no n'hi ha, o està malmès */ }
  return {};
}

/**
 * Opcions de mida i posició per crear una finestra: les desades si encara caben en alguna
 * pantalla (potser s'ha desconnectat un monitor), o les per defecte.
 * @param {string} name  'main' o 'editor'
 * @param {{ width: number, height: number, minWidth: number, minHeight: number }} defaults
 */
function windowBounds(name, defaults) {
  const saved = readWindowStates()[name];
  const valid = saved && ['x', 'y', 'width', 'height'].every((k) => Number.isFinite(saved[k]));
  if (!valid || !isOnSomeDisplay(saved)) return { bounds: { ...defaults }, maximized: false };
  return {
    bounds: {
      ...defaults,
      x: Math.round(saved.x),
      y: Math.round(saved.y),
      width: Math.max(Math.round(saved.width), defaults.minWidth),
      height: Math.max(Math.round(saved.height), defaults.minHeight),
    },
    maximized: saved.maximized === true,
  };
}

/** La barra de títol (almenys 100 × 30 píxels) es veu en alguna pantalla: així sempre es pot moure. */
function isOnSomeDisplay(/** @type {{ x: number, y: number, width: number }} */ b) {
  return screen.getAllDisplays().some(({ workArea: a }) => {
    const width = Math.min(b.x + b.width, a.x + a.width) - Math.max(b.x, a.x);
    const height = Math.min(b.y + 30, a.y + a.height) - Math.max(b.y, a.y);
    return width >= 100 && height >= 30;
  });
}

/** Desa on és la finestra (si està maximitzada, la mida que tenia abans, i que ho estava). */
function saveWindowState(/** @type {string} */ name, /** @type {BrowserWindow} */ win) {
  try {
    const windows = readWindowStates();
    // La principal, sense l'amplada que ha guanyat per al panell lateral (en tornar a obrir-la, és plegat).
    const bounds = win === mainWindow ? mainBoundsWithoutSidePanel() : win.getNormalBounds();
    windows[name] = { ...bounds, maximized: win.isMaximized() };
    // Escriptura atòmica: fitxer temporal i substitució.
    const file = windowStatesFile();
    const temp = `${file}.tmp`;
    fs.writeFileSync(temp, JSON.stringify({ schemaVersion: 1, windows }, null, 2), 'utf8');
    fs.renameSync(temp, file);
  } catch (err) {
    console.warn('No s\'ha pogut desar la posició de la finestra', err);
  }
}

// ---------------------------------------------------------------------------
// Editor (fase 3): una finestra pròpia, que es pot portar a un altre monitor
// ---------------------------------------------------------------------------

/** @type {BrowserWindow | null} */
let editorWindow = null;
let editorCloseConfirmed = false;
/** Fitxer que s'ha d'obrir quan la finestra de l'editor acabi de carregar. */
let editorPendingFile = null;

/** Obre (o porta al davant) la finestra de l'editor; si es passa un fitxer, l'hi obre. */
function openEditorWindow(filePath) {
  if (editorWindow) {
    if (editorWindow.isMinimized()) editorWindow.restore();
    editorWindow.focus();
    if (filePath) editorWindow.webContents.send('editor:open-file', filePath);
    return;
  }

  editorCloseConfirmed = false;
  editorPendingFile = filePath ?? null;
  const { bounds, maximized } = windowBounds('editor', { width: 1400, height: 640, minWidth: 700, minHeight: 360 });
  editorWindow = new BrowserWindow({
    ...bounds,
    show: false,
    title: 'Editor — Ona Bages Play',
    backgroundColor: '#121417',
    icon: APP_ICON,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  editorWindow.removeMenu();
  if (maximized) editorWindow.maximize();
  editorWindow.show();
  editorWindow.loadFile(path.join(__dirname, '..', 'renderer', 'editor.html'));

  editorWindow.webContents.on('before-input-event', (event, input) => {
    if (input.type === 'keyDown' && input.key === 'F12' && !app.isPackaged) {
      editorWindow?.webContents.toggleDevTools();
      event.preventDefault();
    }
  });

  editorWindow.webContents.once('did-finish-load', () => {
    if (editorPendingFile) editorWindow?.webContents.send('editor:open-file', editorPendingFile);
    editorPendingFile = null;
  });

  // Com la principal: primer pregunta a la interfície (si hi ha canvis sense desar, ho confirma).
  editorWindow.on('close', (event) => {
    if (editorCloseConfirmed) {
      if (editorWindow) saveWindowState('editor', editorWindow);
      return;
    }
    event.preventDefault();
    editorWindow?.webContents.send('editor:close-requested');
  });
  editorWindow.webContents.on('render-process-gone', () => {
    editorCloseConfirmed = true;
  });
  editorWindow.on('closed', () => {
    editorWindow = null;
  });
}

ipcMain.handle('editor:open', (_event, filePath) => {
  openEditorWindow(typeof filePath === 'string' && path.isAbsolute(filePath) ? filePath : null);
});

ipcMain.on('editor:close-confirmed', () => {
  editorCloseConfirmed = true;
  editorWindow?.close();
});

let lastEditorFolder = undefined;

/** Diàleg per obrir un MP3 a l'editor. Retorna la ruta, o null. */
ipcMain.handle('dialog:open-mp3', async (event) => {
  const owner = BrowserWindow.fromWebContents(event.sender);
  if (!owner) return null;
  const result = await dialog.showOpenDialog(owner, {
    title: 'Obrir un MP3 a l\'editor',
    defaultPath: lastEditorFolder,
    properties: ['openFile'],
    filters: [{ name: 'MP3', extensions: ['mp3'] }],
  });
  if (result.canceled || result.filePaths.length === 0) return null;
  lastEditorFolder = path.dirname(result.filePaths[0]);
  return result.filePaths[0];
});

/**
 * Diàleg «Desar com…» de l'editor. Proposa «<nom> (editat).mp3» a la mateixa carpeta.
 * Retorna la ruta, o null. Si ja existeix, el diàleg de Windows ja demana si es vol substituir.
 */
ipcMain.handle('dialog:save-mp3', async (event, currentPath) => {
  const owner = BrowserWindow.fromWebContents(event.sender);
  if (!owner || typeof currentPath !== 'string') return null;
  const base = path.basename(currentPath, path.extname(currentPath));
  const result = await dialog.showSaveDialog(owner, {
    title: 'Desar com…',
    defaultPath: path.join(path.dirname(currentPath), `${base} (editat).mp3`),
    filters: [{ name: 'MP3', extensions: ['mp3'] }],
  });
  if (result.canceled || !result.filePath) return null;
  return result.filePath;
});

/** Obre l'Explorador de Windows amb el fitxer seleccionat (menú contextual de la llista). */
ipcMain.handle('shell:show-in-folder', (_event, filePath) => {
  if (typeof filePath === 'string' && path.isAbsolute(filePath)) shell.showItemInFolder(filePath);
});

/**
 * Obre una carpeta a l'Explorador (botons de ⚙ Configuració). Només carpetes que existeixen:
 * mai s'obre un fitxer (podria ser un programa). Retorna un missatge d'error, o '' si s'ha obert.
 */
ipcMain.handle('shell:open-folder', async (_event, folderPath) => {
  if (typeof folderPath !== 'string' || !path.isAbsolute(folderPath)) return 'Ruta no vàlida.';
  try {
    if (!fs.statSync(folderPath).isDirectory()) return 'No és una carpeta.';
  } catch {
    return 'La carpeta encara no existeix.';
  }
  return shell.openPath(folderPath);
});

/**
 * Documentació (fase 5, D-092): fitxers HTML autoportables que s'obren al navegador predeterminat.
 * Desenvolupament: docs\ del repositori. Versió portable: carpeta «documentacio» al costat de l'exe
 * (la hi copia empaquetar.cmd). Només aquests dos noms: la interfície no pot obrir cap altre fitxer.
 */
const HELP_FILES = { manual: 'manual-usuari.html', technical: 'documentacio-tecnica.html' };
const HELP_DIR = app.isPackaged
  ? path.join(path.dirname(process.execPath), 'documentacio')
  : path.join(REPO_ROOT, 'docs');

/** Obre un document d'ajuda. Retorna un missatge d'error, o '' si s'ha obert. */
ipcMain.handle('help:open', async (_event, name) => {
  const file = Object.hasOwn(HELP_FILES, name) ? path.join(HELP_DIR, HELP_FILES[name]) : null;
  if (!file) return 'Document desconegut.';
  if (!fs.existsSync(file)) return `No es troba el document:\n${file}`;
  return shell.openPath(file);
});

/**
 * Carpeta per defecte de les llistes (es crea si no hi és): Documents\Ona Bages Play\Llistes
 * o, en la versió portable, dades\Llistes (perquè viatgin amb l'aplicació).
 */
function playlistsFolder() {
  const folder = IS_PORTABLE
    ? path.join(DATA_DIR, 'Llistes')
    : path.join(app.getPath('documents'), 'Ona Bages Play', 'Llistes');
  try { fs.mkdirSync(folder, { recursive: true }); } catch { /* si no es pot, el diàleg s'obrirà en una altra carpeta */ }
  return folder;
}
let lastPlaylistFolder = undefined;

/** Diàleg per obrir una llista (.obpl, .m3u, .m3u8). Retorna la ruta, o null. */
ipcMain.handle('dialog:open-playlist', async () => {
  if (!mainWindow) return null;
  const result = await dialog.showOpenDialog(mainWindow, {
    title: 'Obrir una llista',
    defaultPath: lastPlaylistFolder ?? playlistsFolder(),
    properties: ['openFile'],
    filters: [
      { name: 'Llistes', extensions: PLAYLIST_EXTENSIONS },
      { name: 'Tots els fitxers', extensions: ['*'] },
    ],
  });
  if (result.canceled || result.filePaths.length === 0) return null;
  lastPlaylistFolder = path.dirname(result.filePaths[0]);
  return result.filePaths[0];
});

let lastRecordingFolder = undefined;

/**
 * Diàleg per desar un enregistrament. Proposa el nom actual, a l'última carpeta on s'ha desat
 * (o, la primera vegada, a la mateixa carpeta on és). Retorna la ruta triada, o null.
 * Si el fitxer triat ja existeix, el motor no el sobreescriu: hi afegeix _2, _3...
 */
ipcMain.handle('dialog:save-recording', async (_event, currentPath) => {
  if (!mainWindow || typeof currentPath !== 'string') return null;
  const folder = lastRecordingFolder ?? path.dirname(currentPath);
  const result = await dialog.showSaveDialog(mainWindow, {
    title: 'Desar l\'enregistrament',
    defaultPath: path.join(folder, path.basename(currentPath)),
    filters: [{ name: 'MP3', extensions: ['mp3'] }],
  });
  if (result.canceled || !result.filePath) return null;
  lastRecordingFolder = path.dirname(result.filePath);
  return result.filePath;
});

/** Diàleg per triar una carpeta (p. ex. la carpeta de treball d'un enregistrador). Retorna la ruta, o null. */
ipcMain.handle('dialog:choose-folder', async (_event, currentFolder) => {
  if (!mainWindow) return null;
  const result = await dialog.showOpenDialog(mainWindow, {
    title: 'Triar la carpeta',
    defaultPath: typeof currentFolder === 'string' ? currentFolder : undefined,
    properties: ['openDirectory', 'createDirectory'],
  });
  if (result.canceled || result.filePaths.length === 0) return null;
  return result.filePaths[0];
});

/** Diàleg per desar una llista (.obpl). Retorna la ruta, o null. */
// --- Perfils en .zip (fase 4, pas 1c; D-061) ---

let lastProfileFolder = undefined;

/** Diàleg per desar un perfil exportat. Proposa «Perfil <nom>.zip» a Documents. Retorna la ruta, o null. */
ipcMain.handle('dialog:save-profile', async (_event, profileName) => {
  if (!mainWindow) return null;
  const name = (typeof profileName === 'string' && profileName ? profileName : 'perfil').replace(/[<>:"/\\|?*]/g, '-');
  const result = await dialog.showSaveDialog(mainWindow, {
    title: 'Exportar el perfil',
    defaultPath: path.join(lastProfileFolder ?? app.getPath('documents'), `Perfil ${name}.zip`),
    filters: [{ name: 'Perfil d\'Ona Bages Play (.zip)', extensions: ['zip'] }],
  });
  if (result.canceled || !result.filePath) return null;
  lastProfileFolder = path.dirname(result.filePath);
  return result.filePath;
});

/** Diàleg per triar un perfil exportat (.zip) per importar-lo. Retorna la ruta, o null. */
ipcMain.handle('dialog:open-profile', async () => {
  if (!mainWindow) return null;
  const result = await dialog.showOpenDialog(mainWindow, {
    title: 'Importar un perfil',
    defaultPath: lastProfileFolder ?? app.getPath('documents'),
    properties: ['openFile'],
    filters: [{ name: 'Perfil d\'Ona Bages Play (.zip)', extensions: ['zip'] }],
  });
  if (result.canceled || result.filePaths.length === 0) return null;
  lastProfileFolder = path.dirname(result.filePaths[0]);
  return result.filePaths[0];
});

/** Carpeta on es desen les llistes per defecte (per proposar-la a la pestanya Llistes del panell lateral). */
ipcMain.handle('app:default-playlists-folder', () => playlistsFolder());

ipcMain.handle('dialog:save-playlist', async (_event, suggestedName) => {
  if (!mainWindow) return null;
  const name = typeof suggestedName === 'string' && suggestedName ? suggestedName : 'Llista';
  const result = await dialog.showSaveDialog(mainWindow, {
    title: 'Desar la llista',
    defaultPath: path.join(lastPlaylistFolder ?? playlistsFolder(), `${name}.obpl`),
    filters: [{ name: 'Llistes d\'Ona Bages Play', extensions: ['obpl'] }],
  });
  if (result.canceled || !result.filePath) return null;
  lastPlaylistFolder = path.dirname(result.filePath);
  return result.filePath;
});
