// @ts-check
// Finestra «Configuració» (botó ⚙ de la barra superior). Docs: protocol.md 4.9, D-041.
// Retorna els ajustos a enviar amb settings.set, o null si es cancel·la.
//
// El nombre de reproductors i enregistradors s'aplica en tornar a arrencar el motor;
// la resta (enregistradors per fila, avís de final, escala) es veu de seguida.

import { ZOOM_STEPS } from './zoom.js';
import { SHORTCUTS_HELP } from './shortcuts.js';
import { escapeHtml } from './format.js';

/** Límits (els mateixos que comprova el motor). */
const MAX_PLAYERS = 6;
const MAX_RECORDERS = 4;
const MAX_END_WARNING = 60;
/** Marge màxim del retall de silencis (ms), com accepta el motor. */
const MAX_TRIM_MARGIN = 2000;
const MAX_PROGRAM_NAME = 60;
/** Dies màxims de l'historial d'enregistraments, com accepta el motor. */
const MAX_ARCHIVE_DAYS = 3650;

/**
 * @param {any} settings  objecte settings del motor
 * @param {{ players: number, recorders: number }} running  quants n'hi ha ara al motor
 * @returns {Promise<{ ui: object, layout: object, program: object, loudness: object, trim: object, recorderLimiter: object, recordingArchive: object, recents: object } | null>}
 */
export function editSettings(settings, running) {
  const options = (/** @type {number} */ max, /** @type {number} */ selected) =>
    Array.from({ length: max }, (_, i) => i + 1)
      .map((n) => `<option value="${n}"${n === selected ? ' selected' : ''}>${n}</option>`).join('');
  const zoomOptions = ZOOM_STEPS
    .map((z) => `<option value="${z}"${Math.abs(z - settings.ui.zoom) < 0.001 ? ' selected' : ''}>${Math.round(z * 100)} %</option>`)
    .join('');
  // Objectius de la normalització (LUFS). Si el desat no hi és, s'hi afegeix.
  const target = Number(settings.loudness?.targetLufs ?? -12);
  const targets = [...new Set([-9, -10, -11, -12, -13, -14, -16, -18, -20, -23, target])].sort((a, b) => b - a);
  // Retall de silencis (fase 5): llindars que mesura el motor i els marges.
  const trim = settings.trim ?? { thresholdDb: -60, leadMs: 10, tailMs: 50, thresholds: [-40, -50, -60, -70, -80] };
  const trimOptions = (trim.thresholds ?? [-40, -50, -60, -70, -80])
    .map((/** @type {number} */ t) => `<option value="${t}"${t === trim.thresholdDb ? ' selected' : ''}>${String(t).replace('-', '−')} dBFS${t === -60 ? ' (recomanat)' : ''}</option>`)
    .join('');
  // Sostre del limitador dels enregistradors (fase 5, D-083). Si el desat no hi és, s'hi afegeix.
  const ceiling = Number(settings.recorderLimiter?.ceilingDb ?? -1);
  const ceilings = [...new Set([-0.1, -0.3, -0.5, -1, -2, -3, ceiling])].sort((a, b) => b - a);
  const ceilingOptions = ceilings
    .map((c) => `<option value="${c}"${c === ceiling ? ' selected' : ''}>${c.toFixed(1).replace('.', ',').replace('-', '−')} dB${c === -1 ? ' (recomanat)' : ''}</option>`)
    .join('');
  // Historial d'enregistraments (fase 5, D-086).
  const archive = settings.recordingArchive ?? { enabled: true, folder: '', defaultFolder: '', keepDays: 30 };
  const loudnessOptions = targets
    .map((t) => `<option value="${t}"${t === target ? ' selected' : ''}>${String(t).replace('-', '−').replace('.', ',')} LUFS${t === -12 ? ' (recomanat)' : ''}</option>`)
    .join('');

  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog obp-form obp-settings';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-gear"></i> Configuració</h2>

      <h3 class="obp-settings-section">Programa</h3>
      <label class="visually-hidden" for="st-program">Nom del programa</label>
      <input id="st-program" class="form-control" type="text" maxlength="${MAX_PROGRAM_NAME}"
             value="${escapeHtml(settings.program?.name ?? '')}" placeholder="Nom del programa (p. ex. Magazín dels dissabtes)">
      <div class="form-text mb-3">Surt a la barra superior i a l'historial del que sona. Es pot deixar buit.</div>

      <h3 class="obp-settings-section">Distribució</h3>
      <div class="obp-settings-grid">
        <label for="st-players">Reproductors</label>
        <select id="st-players" class="form-select form-select-sm">${options(MAX_PLAYERS, settings.layout.players)}</select>

        <label for="st-recorders">Enregistradors</label>
        <select id="st-recorders" class="form-select form-select-sm">${options(MAX_RECORDERS, settings.layout.recorders)}</select>

        <label for="st-per-row">Enregistradors per fila</label>
        <select id="st-per-row" class="form-select form-select-sm">${options(MAX_RECORDERS, settings.ui.recordersPerRow)}</select>

        <label for="st-zoom">Escala de la interfície</label>
        <select id="st-zoom" class="form-select form-select-sm">${zoomOptions}</select>
      </div>
      <div class="message-text mb-2 js-restart"><i class="bi bi-arrow-repeat"></i>
        El nombre de reproductors i enregistradors s'aplicarà quan tornis a obrir l'aplicació
        (ara n'hi ha ${running.players} i ${running.recorders}). Si en treus algun, la seva configuració no s'esborra.
      </div>

      <h3 class="obp-settings-section">Reproductors</h3>
      <div class="obp-settings-grid">
        <label for="st-warning">Avís de final de pista</label>
        <div class="input-group input-group-sm">
          <input id="st-warning" class="form-control" type="number" min="0" max="${MAX_END_WARNING}" step="1"
                 value="${escapeHtml(settings.ui.endWarningSeconds)}">
          <span class="input-group-text">s abans</span>
        </div>
      </div>
      <div class="form-text mb-3">El temps restant es posa vermell i parpelleja. 0 = sense avís.</div>

      <div class="obp-settings-grid">
        <label for="st-loudness">Normalització: objectiu</label>
        <select id="st-loudness" class="form-select form-select-sm">${loudnessOptions}</select>
      </div>
      <div class="form-text mb-3">
        Als reproductors amb el botó NORM encès, cada pista s'apuja o s'abaixa fins a aquest nivell
        (com a molt +${escapeHtml(settings.loudness?.maxBoostDb ?? 10)} dB), abans del DSP. S'aplica a partir de la pista següent.
        Amb el compressor SqrSoft, −12 LUFS deixa la música forta gairebé igual i apuja les pistes fluixes.
      </div>

      <div class="obp-settings-grid">
        <label for="st-trim-threshold">Silencis: llindar</label>
        <select id="st-trim-threshold" class="form-select form-select-sm">${trimOptions}</select>
        <label for="st-trim-lead">Marge abans del so</label>
        <div class="input-group input-group-sm">
          <input id="st-trim-lead" class="form-control" type="number" min="0" max="${MAX_TRIM_MARGIN}" step="5"
                 value="${escapeHtml(trim.leadMs)}">
          <span class="input-group-text">ms</span>
        </div>
        <label for="st-trim-tail">Marge després del so</label>
        <div class="input-group input-group-sm">
          <input id="st-trim-tail" class="form-control" type="number" min="0" max="${MAX_TRIM_MARGIN}" step="10"
                 value="${escapeHtml(trim.tailMs)}">
          <span class="input-group-text">ms</span>
        </div>
      </div>
      <div class="form-text mb-3">
        Als reproductors amb el retall de silencis activat (finestra Processament), cada pista comença al primer so
        menys el marge i s'acaba a l'últim més el marge. Compta com a so qualsevol mostra per sobre del llindar.
        Si hi ha soroll de fons per sobre del llindar, no es retalla res. S'aplica a partir de la pista següent.
      </div>

      <h3 class="obp-settings-section">Enregistradors</h3>
      <div class="obp-settings-grid">
        <label for="st-limiter">Limitador: sostre</label>
        <select id="st-limiter" class="form-select form-select-sm">${ceilingOptions}</select>
      </div>
      <div class="form-text mb-3">
        Als enregistradors amb el botó LIM encès, el que es desa no passa d'aquest nivell: el limitador abaixa el volum
        just abans d'un cop, sense retallar-lo, i per sota del sostre no toca res. S'aplica de seguida.
        Amb −1,0 dB, l'MP3 no passa de 0 dB en reproduir-lo; amb −0,1 dB, en pot passar una mica.
      </div>

      <div class="form-check form-switch mb-1">
        <input id="st-clear-recents" class="form-check-input" type="checkbox" role="switch"${settings.recents?.clearOnExit ? ' checked' : ''}>
        <label class="form-check-label" for="st-clear-recents">Netejar Recents en tancar l'aplicació</label>
      </div>
      <div class="form-text mb-3">
        Es treuen de la llista els enregistraments desats i els que ja no es troben. Els no desats es queden, perquè no
        te n'oblidis. Els fitxers no es toquen.
      </div>

      <div class="form-check form-switch mb-1">
        <input id="st-archive" class="form-check-input" type="checkbox" role="switch"${archive.enabled ? ' checked' : ''}>
        <label class="form-check-label" for="st-archive">Historial: desar una còpia de cada enregistrament</label>
      </div>
      <div class="js-archive-options">
        <input class="form-control form-control-sm mb-1 js-archive-folder" type="text" readonly aria-label="Carpeta de l'historial"
               value="${escapeHtml(archive.folder)}" title="${escapeHtml(archive.folder)}">
        <div class="d-flex flex-wrap gap-2 mb-2">
          <button class="btn btn-sm btn-outline-secondary js-archive-browse" type="button"><i class="bi bi-folder2-open"></i> Canviar…</button>
          <button class="btn btn-sm btn-outline-secondary js-archive-default" type="button" title="Tornar a la carpeta per defecte">Per defecte</button>
          <button class="btn btn-sm btn-outline-secondary js-archive-open" type="button"><i class="bi bi-box-arrow-up-right"></i> Obrir</button>
        </div>
        <div class="obp-settings-grid">
          <label for="st-archive-days">Guardar les còpies</label>
          <div class="input-group input-group-sm">
            <input id="st-archive-days" class="form-control" type="number" min="0" max="${MAX_ARCHIVE_DAYS}" step="1"
                   value="${escapeHtml(archive.keepDays)}">
            <span class="input-group-text">dies</span>
          </div>
        </div>
      </div>
      <div class="form-text mb-3">
        Mentre s'enregistra, se'n fa una còpia en aquesta carpeta (una subcarpeta per dia), que no es toca en desar,
        descartar ni editar. Millor en un altre disc. Les còpies de fa més dels dies triats s'esborren en obrir
        l'aplicació (0 = no s'esborren mai). Els canvis s'apliquen a partir del pròxim enregistrament.
      </div>

      <h3 class="obp-settings-section">Fitxers</h3>
      <div class="d-flex flex-wrap gap-2 mb-1">
        <button class="btn btn-sm btn-outline-secondary js-open-history" type="button">
          <i class="bi bi-folder2-open"></i> Historial del que ha sonat
        </button>
        <button class="btn btn-sm btn-outline-secondary js-open-logs" type="button">
          <i class="bi bi-folder2-open"></i> Registres del motor
        </button>
      </div>
      <div class="form-text mb-3">L'historial és un fitxer CSV per dia (s'obre amb l'Excel). Els registres serveixen per saber què ha passat si hi ha un problema.</div>

      <h3 class="obp-settings-section">Dreceres de teclat</h3>
      <dl class="obp-info-list obp-shortcuts">
        ${SHORTCUTS_HELP.map(([keys, action]) => `<dt><kbd>${escapeHtml(keys)}</kbd></dt><dd>${escapeHtml(action)}</dd>`).join('')}
      </dl>

      <div class="error-text mb-2 js-error"></div>
      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary js-cancel" type="button">Cancel·lar</button>
        <button class="btn btn-primary js-save" value="save"><i class="bi bi-check-lg"></i> Desar</button>
      </div>
    </form>`;

  const $ = (/** @type {string} */ selector) => /** @type {HTMLInputElement} */ (dialog.querySelector(selector));
  const players = $('#st-players');
  const recorders = $('#st-recorders');
  const warning = $('#st-warning');
  const restart = $('.js-restart');
  const error = $('.js-error');

  // L'avís de reiniciar només surt si el nombre triat no és el que té el motor ara.
  const showRestart = () => {
    const changed = Number(players.value) !== running.players || Number(recorders.value) !== running.recorders;
    restart.classList.toggle('d-none', !changed);
  };
  players.addEventListener('change', showRestart);
  recorders.addEventListener('change', showRestart);
  showRestart();

  $('.js-cancel').addEventListener('click', () => dialog.close('cancel'));

  // Historial d'enregistraments: carpeta (triar, per defecte, obrir) i dies. Apagat, les opcions s'enfosqueixen.
  const archiveSwitch = $('#st-archive');
  const archiveFolder = $('.js-archive-folder');
  /** Carpeta triada: '' = la per defecte. */
  let chosenArchiveFolder = archive.folder === archive.defaultFolder ? '' : archive.folder;
  const showArchiveFolder = () => {
    const folder = chosenArchiveFolder || archive.defaultFolder;
    archiveFolder.value = chosenArchiveFolder ? folder : `${folder} (per defecte)`;
    archiveFolder.title = folder;
  };
  const showArchiveEnabled = () => {
    for (const control of dialog.querySelectorAll('.js-archive-options input, .js-archive-options button')) {
      /** @type {HTMLInputElement} */ (control).disabled = !archiveSwitch.checked;
    }
  };
  archiveSwitch.addEventListener('change', showArchiveEnabled);
  showArchiveFolder();
  showArchiveEnabled();
  $('.js-archive-browse').addEventListener('click', async () => {
    const folder = await /** @type {any} */ (window).onabages?.chooseFolder(chosenArchiveFolder || archive.defaultFolder);
    if (folder) {
      chosenArchiveFolder = folder;
      showArchiveFolder();
    }
  });
  $('.js-archive-default').addEventListener('click', () => {
    chosenArchiveFolder = '';
    showArchiveFolder();
  });

  // Obrir les carpetes de l'historial i dels registres (les diu el motor).
  const openFolder = async (/** @type {string | undefined} */ folder) => {
    const bridge = /** @type {any} */ (window).onabages;
    const problem = folder ? await bridge.openFolder(folder) : 'El motor no ha dit on és la carpeta.';
    error.textContent = problem ? `No s'ha pogut obrir la carpeta: ${problem}` : '';
  };
  $('.js-open-history').addEventListener('click', () => openFolder(settings.folders?.history));
  $('.js-archive-open').addEventListener('click', () => openFolder(chosenArchiveFolder || archive.defaultFolder));
  $('.js-open-logs').addEventListener('click', () => openFolder(settings.folders?.logs));

  // Abans de tancar amb «Desar», es comprova l'avís de final (és l'únic camp que s'escriu a mà).
  $('.js-save').addEventListener('click', (e) => {
    const seconds = Number(warning.value);
    if (!Number.isInteger(seconds) || seconds < 0 || seconds > MAX_END_WARNING) {
      e.preventDefault();
      error.textContent = `L'avís de final ha de ser un nombre de segons entre 0 i ${MAX_END_WARNING}.`;
      warning.focus();
      return;
    }
    // Els dies de l'historial també s'escriuen a mà.
    const days = Number($('#st-archive-days').value);
    if (!Number.isInteger(days) || days < 0 || days > MAX_ARCHIVE_DAYS) {
      e.preventDefault();
      error.textContent = `Els dies de l'historial han de ser un nombre entre 0 (no esborrar mai) i ${MAX_ARCHIVE_DAYS}.`;
      $('#st-archive-days').focus();
      return;
    }
    // Els marges del retall de silencis també s'escriuen a mà.
    for (const id of ['#st-trim-lead', '#st-trim-tail']) {
      const ms = Number($(id).value);
      if (!Number.isInteger(ms) || ms < 0 || ms > MAX_TRIM_MARGIN) {
        e.preventDefault();
        error.textContent = `Els marges del retall han de ser un nombre de mil·lisegons entre 0 i ${MAX_TRIM_MARGIN}.`;
        $(id).focus();
        return;
      }
    }
  });

  document.body.appendChild(dialog);
  dialog.showModal();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      resolve(dialog.returnValue === 'save'
        ? {
          ui: {
            zoom: Number($('#st-zoom').value),
            recordersPerRow: Number($('#st-per-row').value),
            endWarningSeconds: Number(warning.value),
          },
          layout: { players: Number(players.value), recorders: Number(recorders.value) },
          program: { name: $('#st-program').value.trim() },
          loudness: { targetLufs: Number($('#st-loudness').value) },
          recorderLimiter: { ceilingDb: Number($('#st-limiter').value) },
          recents: { clearOnExit: $('#st-clear-recents').checked },
          recordingArchive: {
            enabled: archiveSwitch.checked,
            folder: chosenArchiveFolder,
            keepDays: Number($('#st-archive-days').value),
          },
          trim: {
            thresholdDb: Number($('#st-trim-threshold').value),
            leadMs: Number($('#st-trim-lead').value),
            tailMs: Number($('#st-trim-tail').value),
          },
        }
        : null);
      dialog.remove();
    });
  });
}
