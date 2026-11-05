// @ts-check
// Tipus d'arrossegament propis de la interfície (dataTransfer).

/**
 * Fitxers arrossegats des de l'explorador del panell lateral (fase 4, pas 2c): una llista JSON de rutes
 * completes. Les llistes dels reproductors els afegeixen on es deixen anar (playlist.add).
 */
export const PATHS_DRAG_TYPE = 'application/x-obp-paths';

/**
 * Rutes que porta un arrossegament de l'explorador (buit si no n'és un).
 * @param {DataTransfer} dataTransfer
 * @returns {string[]}
 */
export function draggedPaths(dataTransfer) {
  try {
    const paths = JSON.parse(dataTransfer.getData(PATHS_DRAG_TYPE) || '[]');
    return Array.isArray(paths) ? paths.filter((p) => typeof p === 'string') : [];
  } catch {
    return [];
  }
}
