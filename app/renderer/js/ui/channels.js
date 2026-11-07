// @ts-check
// Pistes estèreo amb un canal buit (fase 5, pas 29b; D-132, D-133): textos per al botó MONO del reproductor i
// per a la marca de la fila de la llista. El motor detecta el canal buit i diu com sona (channelFix).

const SIDE = { left: 'esquerre', right: 'dret' };

/**
 * Com està una pista: corregida (sona en mono), amb un canal buit sense corregir, o normal.
 * @param {any} item  una pista de la llista (docs/protocol.md, 4.5)
 * @returns {{ on: boolean, warn: boolean, text: string }}
 */
export function channelState(item) {
  const fix = item?.channelFix ?? null;
  const empty = item?.emptyChannel ?? null;
  const good = empty === 'left' ? 'right' : 'left';
  if (fix === 'left' || fix === 'right') {
    const how = item.bothChannels === true ? 'activat a mà' : 'automàtic';
    return { on: true, warn: false, text: `Només té so pel canal ${SIDE[/** @type {'left' | 'right'} */ (fix)]}: sona pels dos canals (${how}).` };
  }
  if (fix === 'mix') {
    return { on: true, warn: false, text: 'Sona en mono: els dos canals barrejats surten pels dos (activat a mà).' };
  }
  if (empty) {
    return { on: false, warn: true, text: `Només té so pel canal ${SIDE[/** @type {'left' | 'right'} */ (good)]} i la correcció està desactivada: sona només per un costat.` };
  }
  return { on: false, warn: false, text: 'Sona en estèreo, tal com és.' };
}
