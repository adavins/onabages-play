// @ts-check
// Petites funcions per mostrar textos (temps, mides, noms de fitxer).

/** Escapa un text perquè es pugui posar dins d'HTML sense risc. */
export function escapeHtml(/** @type {unknown} */ value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

/** Nom del fitxer, sense la carpeta. */
export function fileName(/** @type {string | null | undefined} */ path) {
  if (!path) return '';
  return path.split(/[\\/]/).pop() ?? path;
}

/** Durada en h:mm:ss (o m:ss si és menys d'una hora). */
export function formatDuration(/** @type {number} */ totalSeconds) {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`;
}
