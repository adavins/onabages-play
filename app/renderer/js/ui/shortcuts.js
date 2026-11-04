// @ts-check
// Dreceres de teclat generals (D-041). Mapa fix; poder-lo canviar queda per a una fase posterior.
//
//   F1, F2… F6        Play / Pausa del reproductor 1, 2… 6 (en pausa, continua)
//   Maj+F1… Maj+F6    Stop del reproductor 1… 6
//
// Funcionen sempre, encara que la llista no estigui seleccionada; no mentre hi ha una finestra
// (diàleg) oberta. Les de la llista (C, D, B, A, P, Supr) només van amb la llista seleccionada.

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

/** Per a la finestra «Configuració»: [tecles, què fan]. */
export const SHORTCUTS_HELP = [
  ['F1, F2… F6', 'Play / Pausa del reproductor 1, 2… 6'],
  ['Maj + F1… F6', 'Stop del reproductor 1… 6'],
  ['Ctrl + / Ctrl − / Ctrl 0', 'Escala de la interfície'],
  ['A la llista: P', 'Preescoltar la pista seleccionada'],
  ['A la llista: C · D · B · A', 'Cue · Duplicar · Bucle · Avanç automàtic'],
  ['A la llista: Supr', 'Treure les pistes seleccionades'],
  ['A la llista: S', 'Inserir una secció a sobre de la selecció'],
  ['A la llista: ← / →', 'Plegar / desplegar la secció seleccionada'],
];

/** Text per a l'ajuda dels botons Play i Stop d'un reproductor (p. ex. «F1»). */
export function playKey(/** @type {string} */ playerId) {
  return Number(playerId) <= 6 ? `F${playerId}` : '';
}

/**
 * @param {Store} store
 * @param {Request} request
 * @param {(playerId: string, text: string) => void} onMessage  mostra un avís al panell del reproductor
 */
export function setupShortcuts(store, request, onMessage) {
  window.addEventListener('keydown', (e) => {
    const match = /^F([1-6])$/.exec(e.key);
    if (!match || e.ctrlKey || e.altKey || e.metaKey) return;
    if (document.querySelector('dialog[open]')) return;
    e.preventDefault();
    if (e.repeat) return; // mantenir premuda la tecla no ha de fer Play-Pausa-Play...

    const id = match[1];
    const player = store.players.get(id);
    if (!player) return;

    let cmd;
    if (e.shiftKey) {
      if (!['playing', 'paused', 'ready'].includes(player.state)) return;
      cmd = 'player.stop';
    } else {
      cmd = player.state === 'playing' ? 'player.pause' : 'player.play';
    }
    request(cmd, { player: id }).catch((err) => onMessage(id, /** @type {Error} */ (err).message));
  });
}
