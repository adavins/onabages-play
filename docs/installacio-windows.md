# Preparació dels ordinadors i inventari del maquinari

## 1. Preparació de Windows (cada ordinador d'estudi)

Molts talls d'àudio no els provoca l'aplicació sinó el sistema. Marca cada punt quan estigui fet.

### Dispositius d'àudio
Tauler de control → So (o `mmsys.cpl`), pestanyes **Reproducció** i **Enregistrament**. Per a cada dispositiu que farà servir Ona Bages Play:

- [ ] **Propietats → Millores** (o «Mejoras de audio»): desactivar-les totes.
- [ ] **Propietats → Opcions avançades → Format predeterminat:** el mateix a tots (per exemple, 24 bits, 48000 Hz). Anota'l a l'inventari.
- [ ] **Propietats → Àudio espacial** («Sonido espacial»): desactivat.
- [ ] **Propietats → Opcions avançades → Mode exclusiu:** es pot deixar com està (l'aplicació fa servir el mode compartit).
- [ ] **Sound Blaster (Creative):** al programari de Creative, desactivar SBX, Crystalizer, ecualitzadors i altres efectes a les sortides d'antena.
- [ ] **Realtek (àudio integrat):** a la consola de Realtek (Realtek Audio Console o «Administrador de sonido HD Realtek»), desactivar la **detecció de connectors** (sovint «Deshabilitar detección de conector del panel frontal» o similar), si l'opció hi és. A les versions personalitzades pel fabricant de la placa (ASUS, MSI…), acostuma a ser a la roda dentada (⚙) de dalt a la dreta; en algunes versions no existeix. Aquesta opció només afecta els connectors del davant: als del darrere, en treure o moure un cable Windows fa desaparèixer el dispositiu i el reproductor es queda sense sortida. **No toquis els cables de la targeta integrada mentre l'aplicació funciona.**
  - Al gestor de Realtek, una sortida o entrada només apareix si hi ha alguna cosa connectada al seu connector (el connector s'il·lumina a la part de baix de la finestra).
- [ ] **So dels monitors (HDMI / DisplayPort):** si no es fa servir, desactivar-lo a l'**Administrador de dispositius** → «Controladores de sonido y vídeo y dispositivos de juego» → «Sonido Intel(R) para pantallas», «NVIDIA High Definition Audio», «AMD High Definition Audio» o similar → clic dret → **Deshabilitar dispositivo**. Desactivar-lo només al tauler de So no n'hi ha prou. Si no es fa, **encendre o apagar el monitor amb el botó** fa que Windows torni a crear el dispositiu, i el sistema d'àudio perd uns mil·lisegons a **totes** les targetes (comprovat: 12 ms perduts en un enregistrament en encendre el monitor).
- [ ] **No apagar ni encendre el monitor amb el botó durant un programa.** Que la pantalla s'apagui sola per estalvi d'energia no afecta l'àudio.
- [ ] Posar un **nom clar** a cada dispositiu (clic dret → Propietats → nom), per exemple «Antena 1», «Antena 2», «Auriculars preescolta», «Taula PGM». Això facilita molt la configuració.

### Sons del sistema
- [ ] Tauler de control → So → pestanya **Sons** → esquema «Sense sons».
- [ ] El dispositiu predeterminat de Windows ha de ser un que **no vagi a antena**.
- [ ] Tauler de control → So → pestanya **Comunicacions**: «**No fer res**». Si no, Windows abaixa el volum de tot quan detecta una trucada (Teams, Skype, DroidCam…).
- [ ] Desactivar (clic dret → Desactivar) les entrades de «bucle» que no es facin servir, com «Mescla estèreo» o «Lo que se oye»: enregistren el que surt per l'ordinador i és fàcil triar-les per error.

### Energia i USB
- [ ] Pla d'energia **Alt rendiment** (o «Rendiment màxim»).
- [ ] Opcions d'energia avançades → Configuració USB → **Suspensió selectiva USB: Desactivada**.
- [ ] Administrador de dispositius → controladors USB (Root Hub) → Administració d'energia → desmarcar «Permetre que l'ordinador apagui aquest dispositiu».
- [ ] No deixar que la pantalla o el disc s'apaguin durant els programes.

### Windows Update i notificacions
- [ ] Configurar les **hores actives** perquè cobreixin l'horari dels programes.
- [ ] Activar «No molestar» (Focus Assist) durant els programes.

### Instal·lar Ona Bages Play (versió portable, D-047)
No hi ha instal·lador: l'aplicació és una carpeta que es copia.
- [ ] Generar el paquet a l'ordinador de desenvolupament: `empaquetar.cmd` → `build\OnaBagesPlay-<versió>.zip`.
- [ ] Si el zip arriba per internet o per correu: botó dret → Propietats → «Desbloquejar», **abans** de descomprimir-lo.
- [ ] Descomprimir-lo en una carpeta on es pugui escriure, p. ex. `D:\OnaBagesPlay\` (**no** a `Program Files`).
- [ ] Obrir `OnaBagesPlay.exe`. Si Windows avisa que no és una aplicació coneguda: «Més informació» → «Executa igualment». Acceptar la drecera a l'escriptori.
- [ ] Windows «N»: instal·lar el Media Feature Pack (sense ell no sonen els MP3).
- [ ] No cal instal·lar el .NET: el motor el porta inclòs.

Tot es desa a la carpeta `dades\` de l'aplicació. **Còpia de seguretat:** copiar `dades\` (o tota la carpeta). **Actualitzar:** tancar amb «Aturar-ho tot», descomprimir la versió nova en una carpeta a part, copiar-hi la carpeta `dades\` de l'antiga i, quan funcioni, esborrar l'antiga. Les instruccions també són al `LLEGEIX-ME.txt` del paquet.

## 2. Inventari del maquinari

Una taula per ordinador. Per al desenvolupament, cal omplir-la per als **ordinadors de proves** (és necessària per a la fase 1). Cada emissora la pot fer servir com a plantilla per documentar els seus equips; l'aplicació no depèn de cap targeta concreta i funciona com a mínim amb l'àudio integrat de la placa base.

**Generar-lo automàticament.** L'script `tools/inventari.ps1` llegeix del registre de Windows tots els dispositius d'àudio i les dades del sistema, i escriu un fitxer `inventari-<ORDINADOR>-<data>.md` amb la taula ja omplerta. Només llegeix; no canvia res de l'ordinador i no cal ser administrador. Des de la carpeta del projecte:

```powershell
powershell -ExecutionPolicy Bypass -File tools\inventari.ps1 -Sortida docs\inventari
```

(La carpeta `docs\inventari` ha d'existir. Sense `-Sortida`, el fitxer es desa a la carpeta actual. Amb `-Tots` s'hi inclouen també els dispositius que Windows recorda però que ja no hi són.)

Després, revisa el fitxer i completa a mà: la ubicació, el disc d'enregistrament, la columna «Ús previst» i la taula de mescles.

Tingues en compte que:

- Moltes targetes integrades (Realtek) **només mostren una sortida o entrada com a activa si hi ha alguna cosa connectada al connector**. Si surt «desconnectat», connecta-hi el cable abans de fer l'inventari. Si s'hi desconnecta el cable mentre l'aplicació funciona, per a l'aplicació és com si la targeta desaparegués.
- «Format a Windows» és el que hi ha a *Propietats → Opcions avançades*. Si diu «predeterminat del fabricant», vol dir que no s'ha triat mai explícitament: tria'l a mà (punt 1) i torna a generar l'inventari.
- «HD Audio» inclou tant l'àudio integrat com algunes targetes PCIe (per exemple, la Sound Blaster Z). Corregeix-ho a mà si cal.

Plantilla, per omplir-la a mà:

### Ordinador: _______________ (estudi / ubicació: _______________)

- Windows (versió i edició): _______________
- Processador / memòria / disc d'enregistrament (SSD o HDD, espai lliure): _______________

| Nom a Windows | Tipus (sortida / entrada) | Targeta o dispositiu | Connexió (PCIe, integrat, USB) | Format a Windows (bits, Hz) | Ús previst (antena 1, preescolta, PGM…) | Observacions |
|---|---|---|---|---|---|---|
| | | | | | | |
| | | | | | | |
| | | | | | | |

Versió del controlador de cada targeta Creative: _______________

### Taula de mescles
- Model: AEQ Capitol IP12 (referència).
- Connexions amb l'ordinador (USB, analògic, altres): _______________
- Freqüència de mostreig de la sortida USB, si se'n fa servir: _______________
