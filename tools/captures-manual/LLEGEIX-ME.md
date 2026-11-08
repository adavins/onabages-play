# Captures del manual

Eines per refer les captures de `docs/manual-usuari.html` amb el **perfil de mostra «Mostra»** (programa «Matins»,
3 reproductors, 2 enregistradors, àudios sintètics amb noms neutres; D-139, D-151).

- Fan servir una **carpeta de dades a part** (`ONABAGES_DATA_DIR`): els perfils i les dades de l'usuari no es toquen.
- Tot el que generen va a `%TEMP%\OnaBagesCaptures` (àudios, dades de mostra i captures), fora del repositori.
- No fan sonar ni enregistren res.
- Sense dependències: només Node (el que ja hi ha) i l'Electron de `app\node_modules`.

## Passos

Amb **Ona Bages Play tancat** (el motor fa servir el port 47800), des d'aquesta carpeta:

```powershell
node genera-audios.mjs        # 1. àudios WAV sintètics (uns 160 MB, només la primera vegada)
node captures.mjs prepara     # 2. crea el perfil «Mostra» des de zero i s'atura (uns 20 s)
node captures.mjs captura     # 3. hi posa les llistes i els ajustos i fa les captures (uns 40 s)
node incrusta.mjs             # 4. les posa dins del manual
```

Entre el pas 3 i el 4, **mira les captures** a `%TEMP%\OnaBagesCaptures\captures`. Si alguna no està bé, no les
incrustis. El pas 3 diu si hi ha hagut errors a la consola de la interfície.

Per comprovar com queda una secció del manual sense obrir el navegador (opcional):

```powershell
..\..\app\node_modules\electron\dist\electron.exe veure-manual.cjs streaming
```

## Quines captures fa

| Captura | Imatge del manual (text alternatiu) |
|---|---|
| `principal` | Finestra principal amb tres reproductors i dos enregistradors |
| `panell-lateral` | Finestra principal amb el panell lateral obert a la pestanya Streaming |
| `enregistrador` | Un enregistrador inactiu |
| `streaming` | Pestanya Streaming del panell lateral |

Les altres imatges del manual (reproductor, processament, Àudios, preescolta, botonera, Recents, editor i
Configuració) es van fer a D-139 i no les fan aquestes eines. Per afegir-ne una:

1. a `captures.mjs`, prepara l'estat (ordres del motor amb `eng(...)`, clics amb `run(...)`) i fes `shot('nom', selector)`;
2. a `incrusta.mjs`, afegeix `nom: 'text alternatiu'` a `IMAGES`;
3. si la imatge és nova, primer afegeix-la a mà al manual (`<figure>` amb un `<img>` i aquest `alt`).

## Coses a saber

- **Mida:** finestra de 1200 × 900 amb la interfície al 80 % (la pàgina en veu 1500 × 1125). Les captures d'un tros
  multipliquen les mides de la pàgina per `devicePixelRatio` (0,8).
- **Dispositius:** els d'aquest ordinador. Si no hi ha la Sound Blaster Z ni la Realtek, agafa el primer que trobi.
- **VS Code** deixa posada la variable `ELECTRON_RUN_AS_NODE` (també és Electron); el script la treu.
- **Icones:** el manual s'obre al navegador sense la font d'icones de l'aplicació. No hi posis `<i class="bi …">`:
  surten buides. Fes servir un caràcter (⚠, ⏸, ⏱…) o una descripció.
