// @ts-check
// Accions sobre un enregistrament acabat (docs/protocol.md, 4.6).
// Les fan servir el panell de l'enregistrador (el pendent) i el panell «Recents».
// Cada funció retorna un missatge per mostrar a l'operador, o null si no cal dir res.

import { choose } from './dialogs.js';
import { fileName, formatBytes, formatDuration } from './format.js';

/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

/**
 * Desar: diàleg de Windows amb el nom i la carpeta proposats; el motor hi mou el fitxer.
 * Mai no se sobreescriu res: si el nom ja existeix, el motor hi afegeix _2, _3...
 * @param {any} recording
 * @param {Request} request
 * @returns {Promise<string | null>}
 */
export async function saveRecording(recording, request) {
  const chosen = await bridge?.saveRecordingFile(recording.path);
  if (!chosen) return null;

  try {
    const result = await request('recording.save', { recordingId: recording.id, path: chosen });
    const wanted = chosen.toLowerCase().endsWith('.mp3') ? chosen : `${chosen}.mp3`;
    if (result.path.toLowerCase() !== wanted.toLowerCase()) {
      return `Ja hi havia un fitxer amb aquest nom: s'ha desat com a «${fileName(result.path)}».`;
    }
    return null;
  } catch (err) {
    return /** @type {Error} */ (err).message;
  }
}

/**
 * Descartar: demana confirmació i envia el fitxer a la paperera de Windows.
 * @param {any} recording
 * @param {Request} request
 * @returns {Promise<string | null>}
 */
export async function discardRecording(recording, request) {
  const choice = await choose({
    title: 'Descartar l\'enregistrament',
    icon: 'bi-trash3',
    message: `${fileName(recording.path)}\n${formatDuration(recording.duration)} · ${formatBytes(recording.fileSize)}\n\n`
      + 'El fitxer s\'enviarà a la paperera de Windows (des d\'allà encara es pot recuperar).',
    buttons: [
      { label: 'Cancel·lar', value: 'cancel' },
      { label: 'Descartar', value: 'discard', style: 'danger', icon: 'bi-trash3' },
    ],
  });
  if (choice !== 'discard') return null;

  try {
    await request('recording.discard', { recordingId: recording.id });
    return null;
  } catch (err) {
    return /** @type {Error} */ (err).message;
  }
}
