// @ts-check
// Escala de la interfície (zoom): Ctrl + / Ctrl − / Ctrl 0 i Ctrl + roda del ratolí.
// El valor es desa al motor (settings.set) i s'aplica amb el zoom d'Electron (preload.js).

/** Passos de zoom disponibles (1 = 100 %). Han d'estar entre 0,7 i 1,5, com accepta el motor. */
const STEPS = [0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

let current = 1.0;

/**
 * @param {(zoom: number) => void} onChange  es crida quan l'usuari canvia el zoom (per desar-lo)
 */
export function setupZoom(onChange) {
  window.addEventListener('keydown', (e) => {
    if (!e.ctrlKey || e.altKey) return;
    if (e.key === '+' || e.key === '=') step(+1);
    else if (e.key === '-') step(-1);
    else if (e.key === '0') change(1.0);
    else return;
    e.preventDefault();
  });

  window.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    step(e.deltaY < 0 ? +1 : -1);
  }, { passive: false });

  /** @param {number} direction */
  function step(direction) {
    const index = STEPS.findIndex((s) => s >= current - 0.001);
    const next = STEPS[Math.min(STEPS.length - 1, Math.max(0, (index < 0 ? 3 : index) + direction))];
    change(next);
  }

  /** @param {number} zoom */
  function change(zoom) {
    if (zoom === current) return;
    applyZoom(zoom);
    onChange(zoom);
  }
}

/** Aplica un zoom (p. ex. el desat al motor) sense tornar-lo a desar. */
export function applyZoom(/** @type {number} */ zoom) {
  current = zoom;
  /** @type {any} */ (window).onabages?.setZoom(zoom);
}

export function currentZoom() {
  return current;
}
