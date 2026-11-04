using System.Text;
using OnaBages.Audio;

// Utilitat de recuperació d'enregistraments d'Ona Bages Play (especificació 8.5).
// Fa el mateix que el motor en arrencar, però sense obrir l'aplicació:
// retalla el fitxer fins a l'últim fotograma MP3 complet, treu l'etiqueta Xing/Info si n'hi ha
// i esborra el marcador .rec. Comparteix el codi amb el motor (OnaBages.Audio/RecordingRecovery.cs).
//
// Ús:
//   OnaBages.Recovery <carpeta>          recupera tots els enregistraments interromputs de la carpeta
//   OnaBages.Recovery <fitxer.mp3>       recupera aquest fitxer (tingui marcador o no)
//   OnaBages.Recovery <fitxer.mp3.rec>   ídem, a partir del marcador
//   OnaBages.Recovery                    busca a la carpeta per defecte (Documents\Ona Bages Play\Enregistraments)
//
// Codi de sortida: 0 = tot bé (o no hi havia res); 1 = algun no s'ha pogut recuperar; 2 = ús incorrecte.

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("Ona Bages Play — recuperació d'enregistraments");
Console.WriteLine();

if (args.Length > 1 || args is ["-h" or "--help" or "/?"])
{
    Console.WriteLine("Ús: OnaBages.Recovery [carpeta | fitxer.mp3 | fitxer.mp3.rec]");
    Console.WriteLine("Sense res, busca a la carpeta per defecte:");
    Console.WriteLine($"  {Recorder.DefaultFolder}");
    return 2;
}

string target = args.Length == 1 ? args[0].Trim('"') : Recorder.DefaultFolder;
List<string> paths;

if (Directory.Exists(target))
{
    paths = [.. RecordingRecovery.FindMarkers(target)];
    Console.WriteLine($"Carpeta: {target}");
    if (paths.Count == 0)
    {
        Console.WriteLine("No hi ha cap enregistrament interromput (cap fitxer .rec). No cal fer res.");
        return 0;
    }
    Console.WriteLine($"Enregistraments interromputs: {paths.Count}");
}
else if (File.Exists(target) || File.Exists(target + RecordingRecovery.MarkerExtension))
{
    paths = [target];
}
else
{
    Console.WriteLine($"No existeix: {target}");
    return 2;
}

bool allOk = true;
foreach (var path in paths)
{
    var result = RecordingRecovery.Recover(path);
    Console.WriteLine();
    Console.WriteLine(Path.GetFileName(result.Path));
    Console.WriteLine($"  {result.Message}");
    if (result.Outcome == RecoveryOutcome.Recovered)
        Console.WriteLine($"  Durada: {result.Duration:hh\\:mm\\:ss} · mida: {result.FileSize / 1024.0 / 1024.0:0.0} MB");
    if (result.Outcome is RecoveryOutcome.Failed or RecoveryOutcome.InUse)
        allOk = false;
}

Console.WriteLine();
Console.WriteLine(allOk ? "Fet." : "Algun enregistrament no s'ha pogut recuperar (vegeu el motiu a sobre).");
return allOk ? 0 : 1;
