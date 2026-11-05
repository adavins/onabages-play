# Ona Bages Play — guia per a Claude Code

Aplicació d'escriptori per a Windows per a la **producció de programes de ràdio en directe**: diversos reproductors amb sortides físiques pròpies, preescolta, diversos enregistradors MP3 simultanis i un editor ràpid (estil mp3DirectCut). Substitueix diverses instàncies de Winamp + mp3DirectCut.

L'especificació completa és a `docs/especificacio.md`. Llegeix-ne la secció corresponent abans de començar cada tasca nova.

**Prioritat absoluta:** l'àudio no es pot interrompre i els enregistraments no es poden perdre.

---

## Fase actual

**Fase 5 — Ampliacions segons l'ús real** (vegeu `docs/especificacio.md`, secció 14.3). Les prioritats les tria el desenvolupador, una a una.
Pendent de les fases 1, 2 i 3 (D-026, D-048, D-056): la prova de 3 hores, la desconnexió d'un USB real, un programa complet en paral·lel amb el Winamp i la prova de l'editor amb el maquinari real.
Ajornat de la fase 4 (D-069, D-073): els panells configurables i l'equalitzador.
Ordinador de proves i repartiment de dispositius: `docs/inventari/`.
Quan una fase s'acabi, actualitza aquesta línia i afegeix una entrada a `docs/decisions.md`.

## Com treballem

- **Idioma:** respon i escriu la documentació en català. Els identificadors del codi van en anglès; els comentaris i tots els textos de la interfície, en català.
- **El desenvolupador:** treballa sol i té poca experiència amb C#; se sent més còmode amb JavaScript pur i Bootstrap. Per tant:
  - prefereix codi llegible i directe a solucions enginyoses;
  - evita patrons i abstraccions que no calguin ara;
  - quan facis un canvi, explica breument què has fet i per què, en llenguatge planer.
- **Passos petits:** cada canvi ha de compilar i ha d'acabar amb una manera concreta de provar-lo. Per a canvis grans, proposa primer un pla breu.
- **Git:** després de cada pas que funcioni, proposa un missatge de commit en català.
- **Maquinari real:** no pots escoltar àudio, connectar dispositius ni veure la finestra. Quan una tasca ho necessiti, digues exactament què ha de fer el desenvolupador i què ha de comprovar. Sempre que es pugui, crea proves automàtiques (per exemple: reproduir un to de prova, enregistrar-lo i analitzar el fitxer per detectar talls).
- **Abast:** implementa només el que demana la fase actual. Si veus una millora per a més endavant, anota-la; no la facis.
- **Dependències:** no n'afegeixis cap sense justificar-ho. La llicència ha de permetre la distribució gratuïta (MIT, BSD, Apache; LGPL només enllaçada dinàmicament, com LAME). Cada dependència nova s'anota a `docs/decisions.md`.

## Arquitectura (no es canvia sense una decisió explícita)

```text
Electron (app/) ── WebSocket 127.0.0.1 + testimoni ── Motor .NET (engine/)
  HTML + Bootstrap + JS pur                            NAudio (WASAPI) + LAME
```

- **Motor** (`engine/`, C# / .NET 10): procés independent. Té tot l'àudio, tot l'estat i totes les màquines d'estats. És la **font de veritat**.
- **Interfície** (`app/`, Electron): només mostra l'estat i envia ordres. **Mai no reprodueix ni processa àudio.**
- Si la interfície es tanca o cau, el motor continua. En tornar a obrir-se, es reconnecta llegint `%APPDATA%\OnaBages\engine.json` i demana `state.get`.

## Regles del motor

- El **fil d'àudio** (callbacks de NAudio/WASAPI) no pot: crear objectes a cada crida, bloquejar, esperar disc o xarxa, ni escriure registres. Lectura de fitxers i escriptura MP3 van en fils separats, amb memòria intermèdia.
- Cada reproductor, la preescolta i cada enregistrador tenen el seu propi flux. Un error en un flux **no pot afectar els altres**.
- Mai no s'utilitza el «dispositiu predeterminat» de Windows per a una sortida d'antena.
- Dispositius: mode compartit de WASAPI. Identificació per id de Windows i, com a alternativa, pel nom.
- Les excepcions dins del motor es capturen per flux i es converteixen en un estat `Error` + un esdeveniment cap a la interfície. El procés no ha de caure mai per un error d'un flux.

## Regles de l'enregistrament

- MP3 directe amb LAME, **320 kbps CBR estèreo** per defecte. Sense WAV intermedi.
- Un sol fitxer per enregistrament, amb bolcat físic al disc (`Flush(true)`) cada 2 s.
- Sense etiqueta Xing/Info (o reescrita en tancar), perquè el fitxer sigui vàlid en tot moment.
- Marcador `<fitxer>.mp3.rec` mentre s'enregistra; s'esborra en aturar correctament.
- Aturar un enregistrament sempre demana confirmació a la interfície.

## Regles de la interfície

- HTML + Bootstrap 5 (tema fosc) + JavaScript pur amb mòduls ES. **Sense React, sense TypeScript, sense empaquetadors.** Es pot fer servir JSDoc i `// @ts-check`.
- Seguretat d'Electron: `contextIsolation: true`, `nodeIntegration: false`. L'accés al sistema passa només per `preload.js`.
- Els estats es mostren amb **color + icona + text**, mai només amb color.
- Les accions que interrompen (Stop amb so, aturar enregistrament, tancar l'aplicació) demanen confirmació.

## Protocol

- Definit a `docs/protocol.md`. **Qualsevol canvi a les ordres o als esdeveniments s'ha d'anotar allà en el mateix canvi.**
- Missatges JSON amb `"v": 1`. Ordres amb `id` i resposta `ok`/`error`; esdeveniments amb `ev`.
- Les ordres noves s'afegeixen a `CommandRouter` del motor, amb la seva prova a `OnaBages.Tests`.

## Dades

- Tot l'accés a dades persistents passa per **un sol mòdul del motor** (repositori). Cap altre codi llegeix o escriu fitxers de configuració.
- Cada fitxer JSON porta `schemaVersion`. Els canvis de format inclouen una migració.
- Escriptura atòmica: fitxer temporal + substitució.
- Els fitxers d'àudio no es copien mai a la configuració: només se'n guarden les rutes.

## Ordres habituals

```powershell
# Motor
dotnet build engine/OnaBages.slnx
dotnet test engine/OnaBages.slnx
dotnet run --project engine/OnaBages.Engine -- --port 47800

# Interfície (des de app/)
npm install
npm start

# Versió portable (des de l'arrel): build\OnaBagesPlay-<versió>.zip (D-047)
.\empaquetar.cmd
```

Per provar el motor sense interfície: arrenca'l i obre `tools/consola-proves.html` al navegador amb el port i el testimoni que mostra la consola.

## Estructura

```text
engine/OnaBages.Audio/      àudio: dispositius, reproductors, enregistradors, DSP (fase 1+)
engine/OnaBages.Engine/     procés del motor: WebSocket, ordres, estat
engine/OnaBages.Recovery/   utilitat de recuperació d'enregistraments (fase 2)
engine/OnaBages.Tests/      proves automàtiques (xUnit)
engine/OnaBages.ToneCheck/  eina de proves: to de prova i anàlisi de talls
app/main/                   procés principal d'Electron i preload
app/renderer/               interfície (HTML, CSS, JS)
tools/                      eines de desenvolupament
docs/                       especificació, protocol, decisions, instal·lació
```

## Documents de referència

- `docs/especificacio.md` — què s'ha de fer i per què.
- `docs/protocol.md` — ordres i esdeveniments entre interfície i motor.
- `docs/decisions.md` — registre de decisions; afegeix-hi una línia per cada decisió nova.
- `docs/installacio-windows.md` — preparació dels ordinadors i inventari del maquinari.
