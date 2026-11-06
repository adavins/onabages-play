'use strict';

// Pont segur entre la interfície i el procés principal.
// La interfície només pot fer servir el que s'exposa aquí (window.onabages).

const { contextBridge, ipcRenderer, webUtils } = require('electron');

contextBridge.exposeInMainWorld('onabages', {
  /** Port i testimoni del motor (l'arrenca si cal). */
  getEngineInfo: () => ipcRenderer.invoke('engine:get-info'),

  /** Torna a comprovar el motor i, si no respon, n'arrenca un de nou. */
  ensureEngine: () => ipcRenderer.invoke('engine:ensure'),

  /** Canvi de perfil: espera que el motor s'aturi i n'arrenca un de nou (mateix port i testimoni). */
  restartEngine: () => ipcRenderer.invoke('engine:restart'),

  /** True una sola vegada si el motor s'ha arrencat en obrir l'aplicació: cal fer triar el perfil (pas 13b). */
  takeStartupChoice: () => ipcRenderer.invoke('app:take-startup-choice'),

  /** L'usuari vol tancar la finestra. */
  onCloseRequested: (callback) => {
    ipcRenderer.on('app:close-requested', () => callback());
  },

  /** Diàleg de Windows per triar fitxers d'àudio (un o diversos). Retorna les rutes (buit si es cancel·la). */
  openAudioFiles: () => ipcRenderer.invoke('dialog:open-audio'),

  /** Diàleg per obrir una llista (.obpl, .m3u, .m3u8). Retorna la ruta, o null. */
  openPlaylistFile: () => ipcRenderer.invoke('dialog:open-playlist'),

  /** Diàleg per desar una llista. Retorna la ruta triada, o null. */
  savePlaylistFile: (suggestedName) => ipcRenderer.invoke('dialog:save-playlist', suggestedName),

  /** Diàleg per desar un enregistrament (proposa el nom actual). Retorna la ruta triada, o null. */
  saveRecordingFile: (currentPath) => ipcRenderer.invoke('dialog:save-recording', currentPath),

  /** Diàleg per desar un perfil exportat (.zip). Retorna la ruta, o null. */
  saveProfileFile: (profileName) => ipcRenderer.invoke('dialog:save-profile', profileName),

  /** Diàleg per triar un perfil exportat (.zip) per importar-lo. Retorna la ruta, o null. */
  openProfileFile: () => ipcRenderer.invoke('dialog:open-profile'),

  /** Carpeta on es desen les llistes per defecte («Llistes»). */
  defaultPlaylistsFolder: () => ipcRenderer.invoke('app:default-playlists-folder'),

  /** Diàleg per triar una carpeta. Retorna la ruta, o null. */
  chooseFolder: (currentFolder) => ipcRenderer.invoke('dialog:choose-folder', currentFolder),

  /** Obre l'Explorador amb aquest fitxer seleccionat. */
  showInFolder: (filePath) => ipcRenderer.invoke('shell:show-in-folder', filePath),

  /** Obre una carpeta a l'Explorador (p. ex. la de l'historial o la dels registres). */
  openFolder: (folderPath) => ipcRenderer.invoke('shell:open-folder', folderPath),

  /** Obre la documentació al navegador predeterminat: 'manual' o 'technical'. Retorna un error o ''. */
  openHelp: (name) => ipcRenderer.invoke('help:open', name),

  /** Ruta completa d'un fitxer arrossegat des de l'Explorador (la pàgina no la pot saber sola). */
  pathForFile: (file) => webUtils.getPathForFile(file),

  /** Escala de la interfície (1 = 100 %, de 0,7 a 1,5). */
  setZoom: (factor) => ipcRenderer.invoke('ui:set-zoom', factor),

  /**
   * Panell lateral obert o tancat (D-062). En obrir-lo, la finestra creix `cssWidth` píxels CSS.
   * Retorna 'grow' (ha crescut), 'overlay' (no podia: el panell va per sobre) o 'closed'.
   */
  setSidePanel: (open, cssWidth) => ipcRenderer.invoke('ui:side-panel', open, cssWidth),

  /** Alçada de la capçalera (píxels CSS), perquè els botons de la finestra hi encaixin (D-060). */
  setTopbarHeight: (cssHeight) => ipcRenderer.send('ui:topbar-height', cssHeight),

  /** Perfil actiu (D-099): la finestra principal es posa on era la d'aquest perfil, i s'hi desa. */
  setWindowProfile: (profileId) => ipcRenderer.send('ui:window-profile', profileId),

  /** La interfície confirma que la finestra es pot tancar. */
  confirmClose: () => ipcRenderer.send('app:close-confirmed'),

  // --- Editor (fase 3) ---

  /** Obre la finestra de l'editor (o la porta al davant), opcionalment amb un fitxer. */
  openEditor: (filePath) => ipcRenderer.invoke('editor:open', filePath ?? null),

  /** Finestra de l'editor: s'hi ha d'obrir aquest fitxer (des de Recents o «Editar»). */
  onEditorOpenFile: (callback) => {
    ipcRenderer.on('editor:open-file', (_event, filePath) => callback(filePath));
  },

  /** Finestra de l'editor: l'usuari la vol tancar. */
  onEditorCloseRequested: (callback) => {
    ipcRenderer.on('editor:close-requested', () => callback());
  },

  /** Finestra de l'editor: es pot tancar. */
  confirmEditorClose: () => ipcRenderer.send('editor:close-confirmed'),

  /** Diàleg per obrir un MP3 a l'editor. Retorna la ruta, o null. */
  openMp3File: () => ipcRenderer.invoke('dialog:open-mp3'),

  /** Diàleg «Desar com…» de l'editor. Retorna la ruta, o null. */
  saveMp3File: (currentPath) => ipcRenderer.invoke('dialog:save-mp3', currentPath),
});
