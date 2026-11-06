// @ts-check
// PIN dels perfils a la interfície (fase 5, pas 13c; D-097).
//
// El PIN es demana per entrar en un perfil, obrir ⚙ Configuració, i canviar el nom, eliminar o importar
// perfils. Mai en les accions del directe. Un cop entrat, el perfil queda desbloquejat 10 minuts
// (només en aquesta finestra, a la memòria: en tornar a obrir l'aplicació es torna a demanar).
// És una protecció contra canvis per error: el motor desa el PIN en text pla i no el comprova.

import { askPin } from './dialogs.js';

/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */
/** @typedef {{ id: string, name: string, hasPin?: boolean }} PinProfile */

/** Quant temps queda desbloquejat un perfil després d'entrar-ne el PIN. */
export const UNLOCK_MINUTES = 10;

/** Perfils desbloquejats: id → fins quan (ms). */
const unlocked = new Map();

/** Avisa ('change') quan es desbloqueja o es torna a bloquejar algun perfil (per al cadenat de la barra). */
export const pinEvents = new EventTarget();

/** @type {number | undefined} */
let timer;

/** @param {string} id */
export function isUnlocked(id) {
  const until = unlocked.get(id);
  return until !== undefined && until > Date.now();
}

/** Minuts que queden del desbloqueig més llarg (0 si no n'hi ha cap). */
export function minutesLeft() {
  const until = Math.max(0, ...unlocked.values());
  return Math.max(0, Math.ceil((until - Date.now()) / 60000));
}

/** El perfil queda desbloquejat {@link UNLOCK_MINUTES} minuts a partir d'ara. @param {string} id */
export function markUnlocked(id) {
  unlocked.set(id, Date.now() + UNLOCK_MINUTES * 60000);
  changed();
}

/**
 * En canviar de perfil: només es manté desbloquejat el perfil on s'entra (s'hi acaba d'entrar el PIN);
 * els altres es tornen a bloquejar. Així, en tornar a un perfil anterior, es torna a demanar el seu PIN.
 * @param {string} id
 */
export function keepOnlyUnlocked(id) {
  for (const other of [...unlocked.keys()]) if (other !== id) unlocked.delete(other);
  changed();
}

/** Torna a demanar el PIN a tots els perfils (cadenat de la barra). */
export function lockAll() {
  unlocked.clear();
  changed();
}

/**
 * Si el perfil té PIN i no està desbloquejat, el demana. Retorna true si es pot continuar.
 * @param {Request} request
 * @param {PinProfile} profile
 * @param {string} why  per a què es demana, p. ex. «obrir la configuració»
 */
export async function requirePin(request, profile, why) {
  if (!profile.hasPin || isUnlocked(profile.id)) return true;
  const ok = await askPin({
    title: profile.name,
    message: `Escriu el PIN del perfil per ${why}:`,
    check: async (pin) => (await request('profile.checkPin', { profile: profile.id, pin })).valid,
  });
  if (ok) markUnlocked(profile.id);
  return ok;
}

/** El perfil actiu, tal com el diu el motor (amb hasPin). @param {Request} request */
export async function activeProfile(request) {
  const { profiles } = await request('profile.list');
  return /** @type {PinProfile & { active: boolean }} */ (profiles.find((/** @type {any} */ p) => p.active));
}

/** Treu els desbloquejos acabats i programa el pròxim avís. */
function changed() {
  const now = Date.now();
  for (const [id, until] of unlocked) if (until <= now) unlocked.delete(id);
  clearTimeout(timer);
  if (unlocked.size > 0) {
    const next = Math.min(...unlocked.values());
    timer = window.setTimeout(changed, next - now + 100);
  }
  pinEvents.dispatchEvent(new Event('change'));
}
