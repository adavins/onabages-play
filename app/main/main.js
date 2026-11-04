'use strict';

// Procés principal d'Electron.
// Responsabilitats: obrir la finestra, assegurar que el motor està en marxa
// i passar-ne la informació de connexió (port i testimoni) a la interfície.
// Aquí NO hi ha àudio: tot l'àudio és al motor .NET.

const { app, BrowserWindow, dialog, ipcMain, shell } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');

const DEFAULT_PORT = 47800;
const ENGINE_START_TIMEOUT_MS = 60_000; // "dotnet run" pot trigar la primera vegada
const REPO_ROOT = path.resolve(__dirname, '..', '..');
const RUNTIME_FILE = path.join(app.getPath('appData'), 'OnaBages', 'engine.json');

/** @type {BrowserWindow | null} */
let mainWindow = null;
/** @type {{ port: number, token: string } | null} */
let engineInfo = null;
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
  mainWindow = new BrowserWindow({
    width: 1600,
    height: 900,
    minWidth: 512,
    minHeight: 340,
    title: 'Ona Bages Play',
    backgroundColor: '#121417',
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

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
    if (closeConfirmed) return;
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
  });
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
  const engineArgs = ['--port', String(port), '--token', token];

  if (process.env.ONABAGES_ENGINE) {
    return { command: process.env.ONABAGES_ENGINE, args: engineArgs };
  }

  const exeName = process.platform === 'win32' ? 'OnaBages.Engine.exe' : 'OnaBages.Engine';
  const candidates = [
    // Aplicació instal·lada (fase 2: l'instal·lador hi copiarà el motor).
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
async function ensureEngine() {
  const existing = readRuntimeFile();
  if (existing && (await isEngineAlive(existing.port))) {
    engineInfo = existing;
    return engineInfo;
  }

  const port = DEFAULT_PORT;
  const token = crypto.randomBytes(24).toString('hex');
  const { command, args } = engineCommand(port, token);

  let spawnError = null;
  const child = spawn(command, args, {
    cwd: REPO_ROOT,
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

ipcMain.handle('engine:ensure', async () => {
  engineInfo = null;
  return ensureEngine();
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
  }
});

ipcMain.on('app:close-confirmed', () => {
  closeConfirmed = true;
  mainWindow?.close();
});

/** Obre l'Explorador de Windows amb el fitxer seleccionat (menú contextual de la llista). */
ipcMain.handle('shell:show-in-folder', (_event, filePath) => {
  if (typeof filePath === 'string' && path.isAbsolute(filePath)) shell.showItemInFolder(filePath);
});

/** Carpeta per defecte de les llistes: Documents\Ona Bages Play\Llistes (es crea si no hi és). */
function playlistsFolder() {
  const folder = path.join(app.getPath('documents'), 'Ona Bages Play', 'Llistes');
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
