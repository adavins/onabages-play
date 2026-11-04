// @ts-check
// Botó 🎧 (llistes, «Recents», enregistrament pendent i tecla P): preescoltar una pista.
// - Si aquesta pista no sona a la preescolta: comença a sonar des del principi (si en sonava
//   una altra, la substitueix).
// - Si ja hi sona: s'atura.

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/**
 * @param {Store} store
 * @param {Request} request
 * @param {string} path  ruta completa del fitxer
 * @returns {Promise<string | null>} el motiu, si el motor ho ha rebutjat (per mostrar-lo al panell)
 */
export async function togglePreview(store, request, path) {
  try {
    if (store.isPreviewing(path)) await request('cue.stop');
    else await request('cue.play', { path });
    return null;
  } catch (err) {
    return `Preescolta: ${/** @type {Error} */ (err).message}`;
  }
}

/**
 * Botó 🎧 per a una fila (llista o «Recents»): encès (taronja) si aquesta pista sona a la preescolta.
 * @param {boolean} on
 * @param {string} attributes  atributs extra (p. ex. data-action="preview")
 * @param {string} className
 */
export function previewButton(on, attributes, className) {
  const title = on ? 'Aturar la preescolta' : 'Preescoltar (pels auriculars)';
  return `<button class="${className} preview-btn${on ? ' on' : ''}" type="button" ${attributes}
    title="${title}" aria-pressed="${on}" draggable="false"><i class="bi bi-headphones"></i></button>`;
}
