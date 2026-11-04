# Notes per a Ona Bages 24h

Preparació del motor d'Ona Bages Play perquè es pugui reutilitzar a Ona Bages 24h (encàrrec: `docs/preparar-motor-ona-bages-24h.md`). **Ona Bages Play és prioritari**: aquí només hi ha revisions, proves i propostes. Cap canvi de codi sense aprovació.

## 1. Frontera de `OnaBages.Audio` (revisió del 4-11-2026)

### 1.1. De què depèn

| Pregunta | Resposta |
|---|---|
| Depèn de `OnaBages.Engine`? | **No.** La referència va a l'inrevés: el motor, `OnaBages.Recovery`, `OnaBages.ToneCheck` i les proves depenen d'`OnaBages.Audio`. |
| Depèn de la configuració (`config.json`, `ConfigRepository`)? | **No.** Tots els ajustos (dispositiu, volum, nom, carpeta) entren per mètodes públics; qui els desa és el motor. |
| Depèn del protocol (JSON de la interfície, WebSocket)? | **No.** Els esdeveniments són `event Action<...>` de C#; la traducció a JSON és a `OnaBages.Engine/Protocol`. |
| Fa servir rutes de `%APPDATA%`? | **No.** L'única ruta fixa és la carpeta per defecte dels enregistraments, `Documents\Ona Bages Play\Enregistraments` (`Recorder.DefaultFolder`). |
| Paquets externs | `NAudio.Wasapi` (MIT), `TagLibSharp` (LGPL, .NET), `libmp3lame.dll` (LGPL, enllaç dinàmic). Res més. |
| Altres lligams | Els ajustos de compilació (`net10.0-windows`, `Nullable`, versió, producte «Ona Bages Play») són a `engine/Directory.Build.props`, **fora** del projecte. Els missatges d'error són en català i pensats per a l'operador (també serveixen per a 24h). |

### 1.2. Què es pot reutilitzar

**Tal qual** (no saben res de llistes ni de Play):

| Classe | Què fa |
|---|---|
| `AudioDevice`, `DeviceList`, `DeviceWatcher`, `DeviceChanges` | Llista de dispositius WASAPI i avisos de connexió i desconnexió |
| `IAudioOutput`, `WasapiAudioOutput` | Sortida cap a un dispositiu concret (mode compartit, 100 ms) |
| `IAudioInput`, `WasapiAudioInput` | Entrada d'un dispositiu (per si 24h ha d'enregistrar o vigilar el so) |
| `AudioFileSource` | Lectura de qualsevol fitxer a estèreo float i a la freqüència de la targeta |
| `TrackInfo` | Durada, format i etiquetes d'un fitxer |
| `PeakMeter`, `SampleRingBuffer` | Mesurador de pics i memòria intermèdia sense bloquejos per al fil d'àudio |
| `LameEncoder`, `Mp3Frames`, `RecordingRecovery`, `Recorder` | Enregistrament MP3 i recuperació (per si 24h ha de gravar l'emissió) |
| `ToneGenerator`, `ToneAnalyzer` | To de prova i detecció de talls (proves llargues) |

**Pensades per a Play** (es poden aprofitar com a model, però 24h en necessitarà unes de pròpies):

| Classe | Per què és de Play |
|---|---|
| `Playlist` | Regles de Play: cue, bucle, avanç automàtic, seccions, «última reproduïda» (D-031, D-033, D-042). |
| `Player` | Un reproductor = una llista + **una sortida pròpia**. 24h necessita moltes fonts barrejades en una sola sortida. |
| `PlayerStream` | Encadena les pistes d'una llista al mateix fil de lectura (D-031), sense solapament. |
| `CuePlayer` | Preescolta d'una pista; 24h no en té. |

**Per separar** (anotat com a tasques; **no es fan sense aprovació**):

- **T-1.** Posar dins d'`OnaBages.Audio.csproj` els ajustos que ara hereta de `engine/Directory.Build.props` (`TargetFramework`, `Nullable`, `ImplicitUsings`, `LangVersion`). Sense això, el projecte no compila fora de la carpeta `engine/`. Canvi petit i sense risc.
- **T-2.** `TrackAnalyzer` escriu el resultat directament en una `Playlist` (`SetProbeResult`). Perquè 24h el pugui fer servir (p. ex. per calcular els punts d'inici, mescla i final), hauria de rebre una funció que digui on va el resultat, en lloc de la llista.
- **T-3.** `Recorder.DefaultFolder` (Documents\Ona Bages Play) l'hauria de poder fixar el motor. Només cal si 24h enregistra.

Una observació per al pas 4: `Player` ja rep la sortida com a paràmetre (`Func<string, IAudioOutput>`). Una «entrada de mesclador» que implementi `IAudioOutput` es podria connectar al `Player` actual **sense canviar-lo**. Es detallarà a les notes de disseny.

### 1.3. Com compartir la biblioteca

| Opció | Avantatges | Inconvenients |
|---|---|---|
| **Submòdul de Git** (`OnaBages.Audio` en un repositori propi, inclòs als dos projectes) | Un sol codi. Un arranjament serveix per als dos. | Cal recordar fer commit al submòdul **i** actualitzar-ne la referència a cada projecte. És fàcil equivocar-s'hi (versió vella, canvis perduts). |
| **Paquet NuGet local** (una carpeta de paquets a l'ordinador) | Versions clares: cada aplicació diu quina fa servir, i un canvi a 24h no pot trencar Play sense voler. | Cada canvi vol: canviar la versió, empaquetar i actualitzar-lo als dos projectes. Lent per provar coses, i la depuració dins la biblioteca és menys directa. |
| **Còpia** | El més senzill: cada projecte té la seva i no hi ha res a configurar. | Els dos codis se separen de seguida; cada arranjament s'ha de fer dues vegades (i és fàcil oblidar-se'n). |
| **Un sol repositori** (24h a la mateixa solució `engine/`, amb el seu motor i la seva interfície en carpetes pròpies) | Una sola `ProjectReference`. `dotnet test` prova les dues aplicacions alhora: si un canvi per a 24h trenca Play, se sap de seguida. El més fàcil per a una persona sola amb Claude Code. | El repositori creix i té dues aplicacions; cal distingir-ne les versions (etiquetes `play-v…` i `24h-v…`). |

**Recomanació:** un sol repositori; si es vol mantenir separat, el submòdul. Ho decidirem quan comenci Ona Bages 24h. La còpia només si 24h acabés sent molt diferent.

## 2. Fitxers que se substitueixen mentre sonen (prova del 4-11-2026)

Prova automàtica: `OnaBages.Tests/FileLockTests.cs`. S'obre una pista de 3 s amb `AudioFileSource`, se'n llegeix 1 s (com si sonés) i un altre procés hi fa una operació. Després es llegeix la resta.

| Operació d'un altre programa | WAV (lector de NAudio) | MP3, M4A, FLAC… (Media Foundation) |
|---|---|---|
| Esborrar-lo | Bloquejat | **Es pot**, i la pista que sonava acaba sencera |
| Reanomenar-lo i posar-ne un de nou amb el mateix nom | Bloquejat | **Es pot**, i la pista que sonava acaba sencera |
| Reanomenar el nou a sobre del vell d'un sol cop (`File.Move` amb substitució) | Bloquejat | Bloquejat («accés denegat») |
| Copiar el nou a sobre (escriure dins del mateix fitxer) | Bloquejat | Bloquejat |
| Qualsevol de les anteriors, un cop tancat | Es pot | Es pot |

**Per què:** el lector de WAV de NAudio obre el fitxer amb `FileShare.Read`, que no deixa ni esborrar-lo ni escriure-hi. Media Foundation l'obre deixant esborrar-lo i reanomenar-lo, però no escriure-hi. Windows no deixa substituir d'un sol cop un fitxer obert, ni tan sols si permet esborrar-lo.

**Què vol dir per a Play (avui):**
- Un MP3 que sona es pot treure de la carpeta (esborrar-lo o reanomenar-lo) i **continua sonant sense talls** fins al final.
- Un WAV que sona no es pot tocar fins que acaba. No és cap problema per al directe: és fins i tot una protecció.
- Ningú no pot escriure a sobre d'un fitxer que sona. És bo: si es pogués, la pista que sona es barrejaria amb la nova.
- La comprovació de pistes (`TrackAnalyzer`) i la preescolta obren el fitxer només una estona; quan el tanquen, es pot tornar a fer tot.

**Proposta per a 24h** (no implementada; cal aprovar-la):
1. **L'Automatitzador substitueix en dos passos:** reanomena el fitxer vell (p. ex. a `.vell`) o l'esborra, i després hi posa el nou. Amb els formats de Media Foundation ja funciona encara que el vell soni.
2. **WAV com la resta de formats:** obrir-los amb un `FileStream` propi amb `FileShare.Read | FileShare.Delete` i passar-lo a `WaveFileReader(Stream)`. Així també es poden esborrar o reanomenar mentre sonen, i ningú no hi pot escriure a sobre. **No** s'ha de permetre `FileShare.Write`: si algú escrivís dins del fitxer que sona, se sentiria una barreja de les dues versions. És un canvi petit a `AudioFileSource.TryOpenWave`, també útil per a Play. Es faria amb la seva prova (els resultats esperats de `FileLockTests` per als WAV canviarien a «es pot» en esborrar i reanomenar).
3. **Fitxers curts a memòria** (senyals horaris, falques, sweepers; p. ex. menys de 20 MB): llegir tot el fitxer a un `MemoryStream` i obrir-lo des d'allà (`WaveFileReader` o `StreamMediaFoundationReader`, que ja són a NAudio). El fitxer queda lliure de seguida, i el disc no pot endarrerir un inici a l'hora exacta.
4. **Fitxers canviats:** en preparar una pista, comparar-ne la mida i la data de modificació amb les de l'última anàlisi. Si han canviat, tornar a calcular la durada i els punts.
