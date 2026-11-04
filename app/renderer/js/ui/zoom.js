// @ts-check
// Escala de la interfície (zoom): Ctrl + / Ctrl − / Ctrl 0 i Ctrl + roda del ratolí.
// El valor es desa al motor (settings.set).
//
// Dues maneres d'escalar:
// - Del 70 % al 150 %: zoom d'Electron (ho fa tot més gran o més petit, també la lletra).
// - Per sota del 70 % (60 % i 50 %): la lletra es queda com al 70 %, perquè no es llegiria bé.
//   El que es fa més petit són els espais, els botons, el comptador gran, els mesuradors...:
//   tot el que a css/app.css es multiplica per --obp-ui-scale.

/** Passos de zoom disponibles (1 = 100 %). Han d'estar entre 0,5 i 1,5, com accepta el motor. */
export const ZOOM_STEPS = [0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

/** Per sota d'aquí la lletra ja no es fa més petita. */
const MIN_TEXT_ZOOM = 0.7;

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
    const index = ZOOM_STEPS.findIndex((s) => s >= current - 0.001);
    const next = ZOOM_STEPS[Math.min(ZOOM_STEPS.length - 1, Math.max(0, (index < 0 ? ZOOM_STEPS.indexOf(1.0) : index) + direction))];
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
  const electronZoom = Math.max(zoom, MIN_TEXT_ZOOM);
  const uiScale = zoom < MIN_TEXT_ZOOM ? zoom / MIN_TEXT_ZOOM : 1;
  document.documentElement.style.setProperty('--obp-ui-scale', String(uiScale));
  /** @type {any} */ (window).onabages?.setZoom(electronZoom);
}

export function currentZoom() {
  return current;
}
