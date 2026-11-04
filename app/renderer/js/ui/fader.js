// @ts-check
// Fader de volum en dB: el d'un reproductor (de −60, silenci, a 0) o el d'enregistrament
// d'un enregistrador (de −60 a +12).
// Doble clic: torna a 0 dB.
// Mentre es mou, s'envia al motor com a molt cada 50 ms (no a cada píxel).

const SEND_EVERY_MS = 50;

/**
 * @typedef {object} FaderOptions
 * @property {number} [min]    valor mínim (silenci), en dB
 * @property {number} [max]    valor màxim, en dB
 * @property {string} [icon]   icona de Bootstrap Icons
 * @property {string} [label]  nom del control (per a l'ajuda i l'accessibilitat)
 */

export class VolumeFader {
  /** @type {HTMLElement} */ element;
  /** @type {HTMLInputElement} */ #input;
  /** @type {HTMLElement} */ #label;
  #onChange;
  #min;
  #dragging = false;
  /** @type {number | undefined} */ #timer;
  /** @type {number | null} */ #pending = null;

  /**
   * @param {(db: number) => void} onChange  envia el volum nou al motor
   * @param {FaderOptions} [options]
   */
  constructor(onChange, { min = -60, max = 0, icon = 'bi-volume-up', label = 'Volum' } = {}) {
    this.#onChange = onChange;
    this.#min = min;
    this.element = document.createElement('div');
    this.element.className = 'fader';
    this.element.innerHTML = `
      <i class="bi ${icon}" title="${label}"></i>
      <input type="range" class="form-range" min="${min}" max="${max}" step="0.5" value="0"
             title="${label} (doble clic: 0 dB)" aria-label="${label}">
      <span class="fader-value">0.0 dB</span>`;

    this.#input = /** @type {HTMLInputElement} */ (this.element.querySelector('input'));
    this.#label = /** @type {HTMLElement} */ (this.element.querySelector('.fader-value'));

    this.#input.addEventListener('pointerdown', () => { this.#dragging = true; });
    this.#input.addEventListener('pointerup', () => { this.#dragging = false; });
    this.#input.addEventListener('input', () => this.#changed(Number(this.#input.value)));
    this.#input.addEventListener('dblclick', () => {
      this.#input.value = '0';
      this.#changed(0);
    });
  }

  /** Valor que diu el motor. Mentre l'operador el mou, no es toca (si no, saltaria). */
  update(/** @type {number} */ db) {
    if (this.#dragging || this.#pending !== null) return;
    this.#input.value = String(db);
    this.#showValue(db);
  }

  #changed(/** @type {number} */ db) {
    this.#showValue(db);
    this.#pending = db;
    if (this.#timer === undefined) this.#flush();
  }

  /** Envia el valor pendent i espera una mica abans d'enviar-ne un altre. */
  #flush() {
    if (this.#pending === null) {
      this.#timer = undefined;
      return;
    }
    this.#onChange(this.#pending);
    this.#pending = null;
    this.#timer = window.setTimeout(() => this.#flush(), SEND_EVERY_MS);
  }

  #showValue(/** @type {number} */ db) {
    const muted = db <= this.#min;
    this.#label.textContent = muted ? '−∞ dB' : `${db > 0 ? '+' : ''}${db.toFixed(1)} dB`;
    this.element.classList.toggle('muted', muted);
    this.element.classList.toggle('lowered', db < 0 && !muted);
    this.element.classList.toggle('boosted', db > 0);
  }
}
