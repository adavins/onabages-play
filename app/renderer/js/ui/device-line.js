// @ts-check
// Línia que diu quin dispositiu té un element, i si hi ha algun problema amb ell.

import { escapeHtml } from './format.js';

/** @typedef {import('../store.js').Store} Store */

/**
 * Només l'avís, si el dispositiu té algun problema (per als panells que ja mostren un desplegable).
 * @param {Store} store
 * @param {string | null} deviceId
 */
export function deviceWarning(store, deviceId) {
  const device = store.device(deviceId);
  return device?.status === 'active' ? '' : deviceLine(store, deviceId);
}

/**
 * @param {Store} store
 * @param {string | null} deviceId
 */
export function deviceLine(store, deviceId) {
  if (!deviceId) {
    return `<div class="device device-missing"><i class="bi bi-exclamation-circle"></i> Sense dispositiu</div>`;
  }

  const device = store.device(deviceId);
  if (!device) {
    return `<div class="device device-missing"><i class="bi bi-exclamation-circle"></i> Dispositiu no disponible</div>`;
  }

  const name = escapeHtml(device.name);
  if (device.status === 'unplugged') {
    return `<div class="device device-warning" title="${name}"><i class="bi bi-plug"></i> ${name} — sense cable</div>`;
  }
  if (device.status === 'disabled') {
    return `<div class="device device-warning" title="${name}"><i class="bi bi-slash-circle"></i> ${name} — desactivat a Windows</div>`;
  }
  return `<div class="device" title="${name}"><i class="bi bi-speaker"></i> ${name}</div>`;
}
