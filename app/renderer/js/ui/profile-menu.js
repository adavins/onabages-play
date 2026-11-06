// @ts-check
// Menú de perfils de la barra superior (fase 4, pas 1b; D-058).
// Un perfil és la configuració de treball d'un programa: reproductors, enregistradors, sortides,
// entrades, llistes... Canviar de perfil reinicia el motor (ho fa app.js amb onSwitch).

import { showContextMenu } from './context-menu.js';
import { choose, askText, pickOne } from './dialogs.js';
import { requirePin, activeProfile } from './pin-lock.js';

/** @typedef {{ id: string, name: string, active: boolean, hasPin?: boolean }} Profile */

/** @type {any} API de preload.js (diàlegs de fitxer) */
const bridge = /** @type {any} */ (window).onabages;

/**
 * @param {{
 *   button: HTMLElement,
 *   request: (cmd: string, params?: object) => Promise<any>,
 *   onSettings: (settings: any) => void,
 *   onSwitch: (profile: { id: string, name: string }) => Promise<void>,
 * }} options
 *   - onSettings: el motor ha tornat ajustos nous (p. ex. en canviar el nom del perfil actiu)
 *   - onSwitch: el motor ha acceptat el canvi i s'està aturant; cal tornar-lo a arrencar
 */
export function setupProfileMenu({ button, request, onSettings, onSwitch }) {
  button.addEventListener('click', openMenu);

  async function openMenu() {
    /** @type {Profile[]} */
    let profiles;
    try {
      profiles = (await request('profile.list')).profiles;
    } catch (err) {
      await showError('Perfils', err);
      return;
    }
    const active = profiles.find((p) => p.active);

    // Sota el botó (també si s'ha obert amb el teclat).
    const rect = button.getBoundingClientRect();
    const where = new MouseEvent('click', { clientX: rect.left, clientY: rect.bottom + 2 });

    showContextMenu(where, [
      ...profiles.map((p) => ({
        label: p.name,
        icon: p.active ? 'bi-check-lg' : 'bi-person',
        active: p.active,
        action: p.active ? undefined : () => switchTo(p),
      })),
      { separator: true },
      { label: 'Perfil nou…', icon: 'bi-plus-lg', action: () => create(null) },
      { label: `Duplicar «${active?.name ?? ''}»…`, icon: 'bi-files', action: () => create(active ?? null) },
      { label: `Canviar el nom de «${active?.name ?? ''}»…`, icon: 'bi-pencil', action: () => rename(active) },
      { label: 'Eliminar un perfil…', icon: 'bi-trash', action: () => remove(profiles) },
      { separator: true },
      { label: 'Exportar un perfil…', icon: 'bi-box-arrow-up', action: () => exportProfile(profiles) },
      { label: 'Importar un perfil…', icon: 'bi-box-arrow-in-down', action: importProfile },
    ]);
  }

  /** Desa un perfil en un .zip (per passar-lo a un altre ordinador o guardar-ne una còpia). @param {Profile[]} profiles */
  async function exportProfile(profiles) {
    // Si n'hi ha més d'un, es tria quin (primer l'actiu).
    let profile = profiles.find((p) => p.active) ?? profiles[0];
    if (profiles.length > 1) {
      const ordered = [profile, ...profiles.filter((p) => p !== profile)];
      const id = await pickOne({
        title: 'Exportar un perfil',
        icon: 'bi-box-arrow-up',
        label: 'Perfil',
        options: ordered.map((p) => ({ value: p.id, label: p.active ? `${p.name} (actiu)` : p.name })),
        message: 'Es desa en un fitxer .zip: la configuració i les últimes llistes dels reproductors.\n'
          + 'Els fitxers d\'àudio no hi van (només les rutes).',
        okLabel: 'Exportar…',
      });
      if (id === null) return;
      profile = /** @type {Profile} */ (profiles.find((p) => p.id === id));
    }

    const path = await bridge?.saveProfileFile(profile.name);
    if (!path) return;
    try {
      await request('profile.export', { profile: profile.id, path });
    } catch (err) {
      await showError('No s\'ha pogut exportar el perfil', err);
      return;
    }
    const choice = await choose({
      title: 'Perfil exportat',
      icon: 'bi-check-circle',
      message: `S'ha desat «${profile.name}» a:\n${path}`,
      buttons: [
        { label: 'D\'acord', value: 'ok', style: 'primary' },
        { label: 'Mostrar a la carpeta', value: 'show', icon: 'bi-folder2-open' },
      ],
    });
    if (choice === 'show') bridge?.showInFolder(path);
  }

  /** Importa un perfil d'un .zip com a perfil nou; si el nom ja existeix, en demana un altre. */
  async function importProfile() {
    // PIN del perfil actiu (pas 13c): importar és un canvi delicat.
    const current = await activeProfile(request).catch(() => null);
    if (current && !(await requirePin(request, current, 'importar un perfil'))) return;
    const path = await bridge?.openProfileFile();
    if (!path) return;

    /** @type {string | undefined} */
    let name;
    let created;
    for (;;) {
      try {
        created = (await request('profile.import', name ? { path, name } : { path })).profile;
        break;
      } catch (err) {
        if (/** @type {any} */ (err).code !== 'profile_exists') {
          await showError('No s\'ha pogut importar el perfil', err);
          return;
        }
        // Ja n'hi ha un amb aquest nom: se'n tria un altre (o es cancel·la).
        const newName = await askText({
          title: 'Importar un perfil',
          icon: 'bi-box-arrow-in-down',
          label: `${/** @type {Error} */ (err).message} Tria un altre nom per al perfil importat:`,
          // El missatge del motor diu el nom repetit: «Ja hi ha un perfil que es diu «X».»
          value: `${name ?? /** @type {Error} */ (err).message.match(/«(.+)»/)?.[1] ?? 'Perfil'} (importat)`,
          okLabel: 'Importar',
        });
        if (newName === null) return;
        name = newName;
      }
    }

    const choice = await choose({
      title: 'Perfil importat',
      icon: 'bi-check-circle',
      message: `S'ha importat «${created.name}».\n`
        + 'Si ve d\'un altre ordinador, revisa les sortides, les entrades i les carpetes dels enregistradors: '
        + 'les que no hi siguin es mostraran en vermell.\nVols canviar-hi ara?',
      buttons: [
        { label: 'Ara no', value: 'no' },
        { label: 'Canviar-hi', value: 'switch', style: 'primary' },
      ],
    });
    if (choice === 'switch') await switchTo({ ...created, active: false }, { confirmed: true });
  }

  /** Perfil nou: buit, o còpia de `from`. Després pregunta si s'hi vol canviar. @param {Profile | null} from */
  async function create(from) {
    const name = await askText({
      title: from ? `Duplicar «${from.name}»` : 'Perfil nou',
      icon: from ? 'bi-files' : 'bi-plus-lg',
      label: 'Nom del perfil (p. ex. el del programa)',
      value: from ? `${from.name} (còpia)` : '',
      okLabel: 'Crear',
    });
    if (name === null) return;

    let created;
    try {
      created = (await request('profile.create', from ? { name, copyFrom: from.id } : { name })).profile;
    } catch (err) {
      await showError('No s\'ha pogut crear el perfil', err);
      return;
    }

    const message = from
      ? `S'ha creat «${created.name}», amb la mateixa configuració que «${from.name}».`
      : `S'ha creat «${created.name}». És buit: hi hauràs de triar les sortides i les entrades.`;
    const choice = await choose({
      title: 'Perfil creat',
      icon: 'bi-check-circle',
      message: `${message}\nVols canviar-hi ara?`,
      buttons: [
        { label: 'Ara no', value: 'no' },
        { label: 'Canviar-hi', value: 'switch', style: 'primary' },
      ],
    });
    if (choice === 'switch') await switchTo({ ...created, active: false }, { confirmed: true });
  }

  /** @param {Profile | undefined} profile */
  async function rename(profile) {
    if (!profile) return;
    if (!(await requirePin(request, profile, 'canviar-li el nom'))) return;
    const name = await askText({
      title: 'Canviar el nom del perfil',
      icon: 'bi-pencil',
      label: 'Nom del perfil',
      value: profile.name,
      okLabel: 'Desar',
    });
    if (name === null || name === profile.name) return;
    try {
      const result = await request('profile.rename', { profile: profile.id, name });
      onSettings(result.settings);
    } catch (err) {
      await showError('No s\'ha pogut canviar el nom', err);
    }
  }

  /** @param {Profile[]} profiles */
  async function remove(profiles) {
    const others = profiles.filter((p) => !p.active);
    if (others.length === 0) {
      await choose({
        title: 'Eliminar un perfil',
        icon: 'bi-info-circle',
        message: 'Només hi ha el perfil actiu, i el perfil actiu no es pot eliminar.',
        buttons: [{ label: 'D\'acord', value: 'ok', style: 'primary' }],
      });
      return;
    }

    const id = await pickOne({
      title: 'Eliminar un perfil',
      icon: 'bi-trash',
      label: 'Perfil',
      options: others.map((p) => ({ value: p.id, label: p.name })),
      message: 'Se n\'esborrarà la configuració i les últimes llistes dels reproductors.\n'
        + 'Els fitxers d\'àudio i els enregistraments no es toquen. El perfil actiu no es pot eliminar.',
      okLabel: 'Eliminar…',
      okStyle: 'danger',
    });
    if (id === null) return;
    const profile = others.find((p) => p.id === id);
    if (!profile || !(await requirePin(request, profile, 'eliminar-lo'))) return;

    const choice = await choose({
      title: 'Eliminar el perfil',
      icon: 'bi-exclamation-triangle-fill',
      message: `Segur que vols eliminar «${profile?.name}»? No es pot desfer.`,
      buttons: [
        { label: 'Cancel·lar', value: 'cancel' },
        { label: 'Eliminar', value: 'delete', style: 'danger' },
      ],
    });
    if (choice !== 'delete') return;
    try {
      await request('profile.delete', { profile: id });
    } catch (err) {
      await showError('No s\'ha pogut eliminar el perfil', err);
    }
  }

  /**
   * Canvia de perfil: el motor s'atura i se'n torna a arrencar un amb el perfil nou.
   * @param {Profile} profile
   * @param {{ confirmed?: boolean }} [options]
   */
  async function switchTo(profile, { confirmed = false } = {}) {
    if (!(await requirePin(request, profile, 'canviar-hi'))) return;
    if (!confirmed) {
      const choice = await choose({
        title: 'Canviar de perfil',
        icon: 'bi-person-badge',
        message: `Canviar al perfil «${profile.name}»?\n`
          + 'El motor es reiniciarà (uns segons) i la pantalla es tornarà a muntar amb els reproductors, '
          + 'els enregistradors i les llistes d\'aquest perfil.',
        buttons: [
          { label: 'Cancel·lar', value: 'cancel' },
          { label: 'Canviar de perfil', value: 'switch', style: 'primary' },
        ],
      });
      if (choice !== 'switch') return;
    }

    try {
      await request('profile.switch', { profile: profile.id });
    } catch (err) {
      // P. ex. hi ha un enregistrament en curs: el motor diu què cal aturar.
      await showError('Ara no es pot canviar de perfil', err);
      return;
    }
    await onSwitch(profile);
  }
}

/** @param {string} title @param {unknown} err */
function showError(title, err) {
  return choose({
    title,
    icon: 'bi-exclamation-triangle-fill',
    message: /** @type {Error} */ (err).message,
    buttons: [{ label: 'D\'acord', value: 'ok', style: 'primary' }],
  });
}
