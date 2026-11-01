'use strict';

// Pont segur entre la interfície i el procés principal.
// La interfície només pot fer servir el que s'exposa aquí (window.onabages).

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('onabages', {
  /** Port i testimoni del motor (l'arrenca si cal). */
  getEngineInfo: () => ipcRenderer.invoke('engine:get-info'),

  /** Torna a comprovar el motor i, si no respon, n'arrenca un de nou. */
  ensureEngine: () => ipcRenderer.invoke('engine:ensure'),

  /** L'usuari vol tancar la finestra. */
  onCloseRequested: (callback) => {
    ipcRenderer.on('app:close-requested', () => callback());
  },

  /** La interfície confirma que la finestra es pot tancar. */
  confirmClose: () => ipcRenderer.send('app:close-confirmed'),
});
