// @ts-check
// Desplegable per triar el dispositiu d'un element (sortida o entrada).
// Només es torna a construir quan canvia la llista o el dispositiu triat:
// si es reconstruís a cada actualització (10 per segon), es tancaria mentre l'operador el fa servir.

import { escapeHtml } from './format.js';

/** @typedef {import('../store.js').Store} Store */

const STATUS_SUFFIX = { unplugged: ' — sense cable', disabled: ' — desactivat' };

export class DeviceSelect {
  /** @type {HTMLSelectElement} */ element;
  #key = '';

  /**
   * @param {'output' | 'input'} kind
   * @param {(deviceId: string) => void} onChoose
   */
  constructor(kind, onChoose) {
    this.kind = kind;
    this.element = document.createElement('select');
    this.element.className = 'form-select form-select-sm device-select';
    this.element.addEventListener('change', () => onChoose(this.element.value));
  }

  /** Obliga a reconstruir-lo (p. ex. si el motor ha rebutjat el canvi i cal tornar a mostrar el d'abans). */
  invalidate() {
    this.#key = '';
  }

  /**
   * @param {Store} store
   * @param {string | null} currentId  dispositiu triat ara
   * @param {boolean} disabled
   */
  update(store, currentId, disabled) {
    this.element.disabled = disabled;

    // Els desactivats a Windows no es poden fer servir: només es mostren si són l'assignat.
    const devices = store.devices.filter((d) => d.kind === this.kind && (d.status !== 'disabled' || d.id === currentId));
    const key = JSON.stringify([currentId, devices.map((d) => [d.id, d.name, d.status])]);
    if (key === this.#key) return;
    this.#key = key;

    const options = [];
    if (!currentId) {
      options.push(`<option value="" selected disabled>— Tria ${this.kind === 'output' ? 'una sortida' : 'una entrada'} —</option>`);
    } else if (!devices.some((d) => d.id === currentId)) {
      options.push(`<option value="${escapeHtml(currentId)}" selected>Dispositiu no disponible</option>`);
    }
    for (const d of devices) {
      const suffix = /** @type {Record<string, string>} */ (STATUS_SUFFIX)[d.status] ?? '';
      const selected = d.id === currentId ? ' selected' : '';
      options.push(`<option value="${escapeHtml(d.id)}"${selected}>${escapeHtml(d.name + suffix)}</option>`);
    }
    this.element.innerHTML = options.join('');
  }
}
