<#
.SYNOPSIS
  Inventari del maquinari d'àudio d'aquest ordinador, en format Markdown.

.DESCRIPTION
  Llegeix del registre de Windows totes les sortides i entrades d'àudio
  (nom, estat, format, controlador, tipus de connexió) i algunes dades del
  sistema (Windows, processador, memòria, discos, pla d'energia).
  Genera un fitxer .md amb la mateixa taula que docs/installacio-windows.md.

  Només llegeix: no canvia res de l'ordinador. No cal ser administrador.

.PARAMETER Sortida
  Carpeta on es desa el fitxer. Per defecte, la carpeta actual.

.PARAMETER Tots
  Inclou també els dispositius que Windows recorda però que ja no hi són.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\inventari.ps1
#>
param(
  [string]$Sortida = (Get-Location).Path,
  [switch]$Tots
)

$ErrorActionPreference = 'Stop'

# Claus de propietats de Windows (PKEY) tal com apareixen al registre.
$KEY_DESCRIPTION    = '{a45c254e-df1c-4efd-8020-67d146a850e0},2'   # p. ex. «Altaveus»
$KEY_INTERFACE_NAME = '{b3f8fa53-0004-438e-9003-51a46e139bfc},6'   # p. ex. «Realtek High Definition Audio»
$KEY_PARENT_DEVICE  = '{b3f8fa53-0004-438e-9003-51a46e139bfc},2'   # dispositiu físic (HDAUDIO\..., USB\...)
$KEY_DRIVER         = '{83da6326-97a6-4088-9453-a1923f573b29},3'   # inf:...:model:versió:...
$KEY_FORM_FACTOR    = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0'
$KEY_DEVICE_FORMAT  = '{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0'   # format del mode compartit
$KEY_OEM_FORMAT     = '{e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04},3'   # format predeterminat del fabricant

$FORM_FACTORS = @{
  0 = 'xarxa'; 1 = 'altaveus'; 2 = 'línia'; 3 = 'auriculars'; 4 = 'micròfon'
  5 = 'auriculars amb micròfon'; 6 = 'telèfon'; 7 = 'digital'; 8 = 'S/PDIF'
  9 = 'pantalla (HDMI/DisplayPort)'; 10 = 'desconegut'
}

function Get-StateText([int]$state) {
  switch ($state -band 0x0F) {
    1 { 'actiu' }
    2 { 'desactivat' }
    4 { 'no present' }
    8 { 'desconnectat' }
    default { "desconegut ($state)" }
  }
}

function Get-ConnectionText([string]$parent) {
  # El valor comença amb «{n}.»: el traiem i mirem l'enumerador.
  $p = ($parent -replace '^\{\d+\}\.', '').ToUpperInvariant()
  if ($p.StartsWith('USB'))     { return 'USB' }
  if ($p.StartsWith('HDAUDIO')) { return 'HD Audio (integrat o PCIe)' }
  if ($p.StartsWith('PCI'))     { return 'PCIe' }
  if ($p.StartsWith('BTH'))     { return 'Bluetooth' }
  if ($p.StartsWith('SWD') -or $p.StartsWith('ROOT')) { return 'virtual (programari)' }
  if ($p -eq '') { return '' }
  return $p.Split('\')[0]
}

# El format es desa com un PROPVARIANT: 8 bytes de capçalera i després un WAVEFORMATEX.
function Get-FormatText([byte[]]$blob) {
  if ($null -eq $blob -or $blob.Length -lt 26) { return '' }
  $o = 8
  $tag      = [BitConverter]::ToUInt16($blob, $o)
  $channels = [BitConverter]::ToUInt16($blob, $o + 2)
  $rate     = [BitConverter]::ToUInt32($blob, $o + 4)
  $bits     = [BitConverter]::ToUInt16($blob, $o + 14)
  $isFloat  = ($tag -eq 3)

  # WAVE_FORMAT_EXTENSIBLE: bits vàlids i subtipus (PCM o coma flotant).
  if ($tag -eq 0xFFFE -and $blob.Length -ge $o + 40) {
    $valid = [BitConverter]::ToUInt16($blob, $o + 18)
    if ($valid -gt 0) { $bits = $valid }
    $isFloat = ($blob[$o + 24] -eq 3)
  }

  $text = "$bits bits, $rate Hz, $channels canals"
  if ($channels -eq 1) { $text = "$bits bits, $rate Hz, 1 canal" }
  if ($isFloat) { $text += ' (coma flotant)' }
  return $text
}

function Get-DriverVersion([string]$driver) {
  # Exemple: oem54.inf:f823147168e10567:IntcAudModel:10.27.0.12:HDAUDIO\...
  if (-not $driver) { return '' }
  $parts = $driver.Split(':')
  if ($parts.Length -ge 4) { return "$($parts[3]) ($($parts[0]))" }
  return $driver
}

function Get-Endpoints([string]$flow) {
  $root = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\$flow"
  $prefix = if ($flow -eq 'Render') { '{0.0.0.00000000}' } else { '{0.0.1.00000000}' }
  $type = if ($flow -eq 'Render') { 'sortida' } else { 'entrada' }

  foreach ($key in Get-ChildItem $root -ErrorAction SilentlyContinue) {
    $state = [int](Get-ItemProperty $key.PSPath).DeviceState
    if (-not $Tots -and (($state -band 0x0F) -eq 4)) { continue }

    $props = Get-Item (Join-Path $key.PSPath 'Properties') -ErrorAction SilentlyContinue
    if ($null -eq $props) { continue }

    $description = [string]$props.GetValue($KEY_DESCRIPTION)
    $interface   = [string]$props.GetValue($KEY_INTERFACE_NAME)
    $formFactor  = $props.GetValue($KEY_FORM_FACTOR)

    # Si Windows no ha desat cap format triat, fem servir el del fabricant.
    $format = Get-FormatText $props.GetValue($KEY_DEVICE_FORMAT)
    if (-not $format) {
      $format = Get-FormatText $props.GetValue($KEY_OEM_FORMAT)
      if ($format) { $format += ' (predeterminat del fabricant)' }
    }

    $name = $description
    if ($interface) { $name = "$description ($interface)" }

    [pscustomobject]@{
      Name       = $name
      Type       = $type
      State      = Get-StateText $state
      Active     = (($state -band 0x0F) -eq 1)
      Card       = $interface
      Connection = Get-ConnectionText ([string]$props.GetValue($KEY_PARENT_DEVICE))
      Format     = $format
      Driver     = Get-DriverVersion ([string]$props.GetValue($KEY_DRIVER))
      FormFactor = if ($null -ne $formFactor -and $FORM_FACTORS.ContainsKey([int]$formFactor)) { $FORM_FACTORS[[int]$formFactor] } else { '' }
      Id         = "$prefix.$($key.PSChildName)"
    }
  }
}

function ConvertTo-MdCell([string]$text) {
  # Evita que una barra vertical d'un nom trenqui la taula Markdown.
  return ($text -replace '\|', '\|')
}

function Try-Get([scriptblock]$block) {
  try { return (& $block) } catch { return '(no disponible)' }
}

# ---------------------------------------------------------------------------
# Dades del sistema
# ---------------------------------------------------------------------------

$os = Try-Get { Get-CimInstance Win32_OperatingSystem }
$windows = Try-Get {
  $displayVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion
  "$($os.Caption) $displayVersion (compilació $($os.BuildNumber))"
}
$cpu = Try-Get {
  $c = Get-CimInstance Win32_Processor | Select-Object -First 1
  "$($c.Name.Trim()), $($c.NumberOfCores) nuclis / $($c.NumberOfLogicalProcessors) fils"
}
$ram = Try-Get { '{0:N1} GB' -f ($os.TotalVisibleMemorySize / 1MB) }
$power = Try-Get { ((powercfg /getactivescheme) -replace '^.*\(|\)\s*$', '').Trim() }

$disks = Try-Get {
  foreach ($v in Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter } | Sort-Object DriveLetter) {
    $media = Try-Get {
      $disk = Get-Partition -DriveLetter $v.DriveLetter | Get-Disk
      $pd = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq [string]$disk.Number }
      [string]$pd.MediaType
    }
    if ($media -eq 'Unspecified' -or -not $media) { $media = 'tipus desconegut' }
    '{0}: {1} ({2:N0} GB lliures de {3:N0} GB)' -f $v.DriveLetter, $media, ($v.SizeRemaining / 1GB), ($v.Size / 1GB)
  }
}

# ---------------------------------------------------------------------------
# Dispositius d'àudio
# ---------------------------------------------------------------------------

$devices = @(Get-Endpoints 'Render') + @(Get-Endpoints 'Capture') |
  Sort-Object @{ Expression = 'Active'; Descending = $true }, Type, Name

# ---------------------------------------------------------------------------
# Document Markdown
# ---------------------------------------------------------------------------

$computer = $env:COMPUTERNAME
$date = Get-Date -Format 'yyyy-MM-dd'
$lines = New-Object System.Collections.Generic.List[string]

$lines.Add("### Ordinador: $computer (estudi / ubicació: _______________)")
$lines.Add('')
$lines.Add("Inventari generat automàticament el $date amb ``tools/inventari.ps1``.")
$lines.Add('')
$lines.Add("- Windows (versió i edició): $windows")
$lines.Add("- Processador: $cpu")
$lines.Add("- Memòria: $ram")
$lines.Add('- Discos:')
foreach ($d in @($disks)) { $lines.Add("  - $d") }
$lines.Add("- Pla d'energia actiu: $power")
$lines.Add('- Disc d''enregistrament: _______________')
$lines.Add('')
$lines.Add('| Nom a Windows | Tipus | Estat | Targeta o dispositiu | Connexió | Format a Windows | Controlador | Ús previst (antena 1, preescolta, PGM…) | Observacions |')
$lines.Add('|---|---|---|---|---|---|---|---|---|')
foreach ($d in $devices) {
  $obs = if ($d.FormFactor) { "connector: $($d.FormFactor)" } else { '' }
  $lines.Add("| $(ConvertTo-MdCell $d.Name) | $($d.Type) | $($d.State) | $(ConvertTo-MdCell $d.Card) | $($d.Connection) | $($d.Format) | $(ConvertTo-MdCell $d.Driver) | | $obs |")
}
if ($devices.Count -eq 0) {
  $lines.Add('| (no s''ha trobat cap dispositiu d''àudio) | | | | | | | | |')
}
$lines.Add('')
$lines.Add('Identificadors de Windows (els que farà servir l''aplicació per reconèixer cada dispositiu):')
$lines.Add('')
foreach ($d in $devices) { $lines.Add("- $(ConvertTo-MdCell $d.Name) — ``$($d.Id)``") }
$lines.Add('')
$lines.Add('### Taula de mescles')
$lines.Add('- Model: _______________')
$lines.Add('- Connexions amb l''ordinador (USB, analògic, altres): _______________')
$lines.Add('- Freqüència de mostreig de la sortida USB, si se''n fa servir: _______________')

$file = Join-Path $Sortida "inventari-$computer-$date.md"
[System.IO.File]::WriteAllLines($file, $lines, (New-Object System.Text.UTF8Encoding $false))

$active = @($devices | Where-Object { $_.Active })
Write-Host ''
Write-Host "Inventari desat a: $file"
Write-Host ("Dispositius d'àudio: {0} actius de {1} trobats." -f $active.Count, $devices.Count)
Write-Host ''
$devices | Format-Table @{ Label = 'Nom'; Expression = 'Name' }, @{ Label = 'Tipus'; Expression = 'Type' },
  @{ Label = 'Estat'; Expression = 'State' }, @{ Label = 'Format'; Expression = 'Format' } -AutoSize
