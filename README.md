# Ona Bages Play

Aplicació d'escriptori per a Windows per a la producció de programes de ràdio en directe: reproductors amb sortides pròpies, preescolta, enregistradors MP3 simultanis i editor ràpid.

```text
Electron (app/) ── WebSocket 127.0.0.1 + testimoni ── Motor .NET (engine/)
  HTML + Bootstrap + JS pur                            NAudio (WASAPI) + LAME
```

Documentació:

- [`docs/especificacio.md`](docs/especificacio.md): què fa l'aplicació i per què.
- [`docs/protocol.md`](docs/protocol.md): ordres i esdeveniments entre la interfície i el motor.
- [`docs/decisions.md`](docs/decisions.md): registre de decisions.
- [`docs/installacio-windows.md`](docs/installacio-windows.md): preparació dels ordinadors d'estudi.
- [`docs/manual-usuari.html`](docs/manual-usuari.html) i [`docs/documentacio-tecnica.html`](docs/documentacio-tecnica.html): manual i documentació tècnica (també al botó «?» de l'aplicació).
- [`CLAUDE.md`](CLAUDE.md): regles per treballar amb Claude Code.

---

## 1. Requisits

Només Windows 10 o 11 (x64): l'àudio fa servir WASAPI.

| Programa | Versió | Per a què |
|---|---|---|
| [Git](https://git-scm.com) | qualsevol | clonar el projecte |
| [Node.js](https://nodejs.org) | LTS | la interfície (Electron) |
| [.NET SDK](https://dotnet.microsoft.com/download) | **10** | el motor i les proves |
| [Visual Studio Code](https://code.visualstudio.com) | opcional | editar; en obrir la carpeta proposa les extensions recomanades (C#, EditorConfig, Claude Code) |

Comprova-ho en un terminal:

```powershell
git --version
node --version
npm --version
dotnet --version   # ha de començar per 10.
```

No cal instal·lar res més: la biblioteca LAME (`libmp3lame.dll`, LGPL) ja és al repositori, a `engine/OnaBages.Audio/native/`, i NAudio s'instal·la sola amb el primer `dotnet build`.

## 2. Clonar i preparar el projecte (una sola vegada)

```powershell
# 1. Clonar
git clone https://github.com/adavins/onabages-play.git
cd onabages-play

# 2. Motor: descarrega els paquets NuGet i compila
dotnet build engine/OnaBages.slnx

# 3. Comprovar que tot funciona (unes 650 proves, prop d'un minut)
dotnet test engine/OnaBages.slnx

# 4. Interfície: instal·la Electron i Bootstrap a app/node_modules
cd app
npm install
cd ..
```

Si `dotnet test` falla en una prova de temps (per exemple, un fos o la reproducció de l'editor), torna-la a executar: alguna prova d'àudio és sensible si l'ordinador va molt carregat.

## 3. Arrencar l'aplicació (desenvolupament)

Amb doble clic a **`arrencar.cmd`**, o des de l'arrel del projecte:

```powershell
.\arrencar.cmd
```

L'script:

1. compila el motor, perquè l'aplicació faci servir sempre l'última versió;
2. si encara no hi és, fa `npm install` a `app/`;
3. obre la interfície (`npm start`), que arrenca el motor sola.

Si l'aplicació ja era oberta, el motor no es pot compilar (els seus fitxers estan en ús) i l'script ho avisa: tanca l'aplicació amb **«Aturar-ho tot»** i torna'l a executar.

Les dades (configuració, perfils, llistes, registres) es desen a `%APPDATA%\OnaBages`.

## 4. Empaquetar la versió portable

Amb doble clic a **`empaquetar.cmd`**, o des de l'arrel del projecte:

```powershell
.\empaquetar.cmd
```

Genera:

- `build\OnaBagesPlay\`: la carpeta a punt per fer servir (`OnaBagesPlay.exe`);
- `build\OnaBagesPlay-<versió>.zip`: el mateix, comprimit per distribuir-lo.

El motor s'hi inclou amb el .NET (l'ordinador d'estudi no l'ha de tenir instal·lat), i també l'eina de recuperació, el manual i la documentació tècnica. La versió surt de `app/package.json`. Cal tenir tancada l'aplicació empaquetada anterior, perquè l'script esborra la carpeta `build`.

La versió portable desa les dades a la seva carpeta `dades`. Instruccions d'ús: `tools/portable/LLEGEIX-ME.txt` (va dins del zip).

## 5. Altres ordres útils

### Motor

```powershell
dotnet build engine/OnaBages.slnx                     # compilar-ho tot
dotnet test engine/OnaBages.slnx                      # totes les proves
dotnet test engine/OnaBages.slnx --filter EditorNormalizeTests   # només una classe de proves
dotnet run --project engine/OnaBages.Engine -- --port 47800      # arrencar el motor sol
```

Opcions del motor: `--port <port>`, `--token <testimoni>` i `--data-dir <carpeta>`.

Per provar el motor **sense interfície**: arrenca'l sol, obre `tools/consola-proves.html` al navegador, posa-hi el port i el testimoni que mostra la consola i prem **Connectar**.

Si l'aplicació és oberta i vols compilar o passar les proves igualment, fes-ho en una altra carpeta perquè no topi amb els fitxers en ús:

```powershell
dotnet test engine/OnaBages.slnx --artifacts-path $env:TEMP\onabages-proves
```

### Interfície

```powershell
cd app
npm start                                            # obrir la interfície (arrenca el motor si cal)
$env:ONABAGES_DATA_DIR = "D:\proves\dades"; npm start   # amb una carpeta de dades de proves (no toca les de debò)
```

### Eines de proves d'àudio (`OnaBages.ToneCheck`)

```powershell
dotnet run --project engine/OnaBages.ToneCheck -- generar to.wav 180          # to de prova de 3 hores
dotnet run --project engine/OnaBages.ToneCheck -- analitzar enregistrament.mp3 # busca talls i silencis
dotnet run --project engine/OnaBages.ToneCheck -- convivencia 60              # editor + 2 enregistradors alhora
dotnet run --project engine/OnaBages.ToneCheck -- sonoritat pista.mp3         # LUFS i pic
dotnet run --project engine/OnaBages.ToneCheck -- processar entrada.mp3 sortida.wav   # compressor/limitador
```

La prova de 3 hores: reprodueix `to.wav` per un reproductor, enregistra-ho amb un enregistrador connectat a aquella sortida i analitza l'MP3 amb `analitzar`.

### Recuperar enregistraments interromputs

Normalment l'aplicació els recupera sola en arrencar. A mà:

```powershell
dotnet run --project engine/OnaBages.Recovery -- "C:\carpeta\dels\enregistraments"
dotnet run --project engine/OnaBages.Recovery -- "C:\...\Programa.mp3.rec"
```

Sense cap argument, revisa la carpeta d'enregistraments per defecte. A la versió portable és `OnaBages.Recovery.exe`, dins de `resources\engine`.

### Inventari del maquinari

Llista les sortides i entrades d'àudio de l'ordinador en Markdown, per omplir `docs/installacio-windows.md` (només llegeix, no cal ser administrador):

```powershell
powershell -ExecutionPolicy Bypass -File tools\inventari.ps1
```
