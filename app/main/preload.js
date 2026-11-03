'use strict';

// Pont segur entre la interfície i el procés principal.
// La interfície només pot fer servir el que s'exposa aquí (window.onabages).

const { contextBridge, ipcRenderer, webUtils } = require('electron');

contextBridge.exposeInMainWorld('onabages', {
  /** Port i testimoni del motor (l'arrenca si cal). */
  getEngineInfo: () => ipcRenderer.invoke('engine:get-info'),

  /** Torna a comprovar el motor i, si no respon, n'arrenca un de nou. */
  ensureEngine: () => ipcRenderer.invoke('engine:ensure'),

  /** L'usuari vol tancar la finestra. */
  onCloseRequested: (callback) => {
    ipcRenderer.on('app:close-requested', () => callback());
  },

  /** Diàleg de Windows per triar un fitxer d'àudio. Retorna la ruta, o null si es cancel·la. */
  openAudioFile: () => ipcRenderer.invoke('dialog:open-audio'),

  /** Ruta completa d'un fitxer arrossegat des de l'Explorador (la pàgina no la pot saber sola). */
  pathForFile: (file) => webUtils.getPathForFile(file),

  /** Escala de la interfície (1 = 100 %, de 0,7 a 1,5). */
  setZoom: (factor) => ipcRenderer.invoke('ui:set-zoom', factor),

  /** La interfície confirma que la finestra es pot tancar. */
  confirmClose: () => ipcRenderer.send('app:close-confirmed'),
});
