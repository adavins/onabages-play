using OnaBages.Audio;

namespace OnaBages.Engine;

/// <summary>
/// En arrencar el motor: busca enregistraments que es van interrompre (marcadors <c>.rec</c>),
/// els recupera i els afegeix a la llista de recents amb l'etiqueta «Recuperat» (especificació 8.5).
/// </summary>
public static class StartupRecovery
{
    /// <summary>
    /// Carpetes on es busca: la de treball de cada enregistrador, la de per defecte i les dels
    /// enregistraments que ja són a la llista (un fitxer tallat per disc ple, per exemple).
    /// <paramref name="savedFolders"/>: les carpetes desades a la configuració, també les d'enregistradors
    /// que s'han tret (pas 9): si se'n treu un, els seus enregistraments interromputs també es recuperen.
    /// </summary>
    public static IReadOnlyList<string> FoldersToScan(IEnumerable<Recorder> recorders, RecordingList list,
        IEnumerable<string?>? savedFolders = null)
    {
        var folders = recorders.Select(r => r.Folder)
            .Concat(savedFolders ?? [])
            .Append(Recorder.DefaultFolder)
            .Concat(list.Items.Select(i => Path.GetDirectoryName(i.Path)));

        var result = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) continue;
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
        }
        return result;
    }

    /// <summary>Recupera tots els marcadors d'aquestes carpetes. Els recuperats van a la llista.</summary>
    public static List<RecoveryResult> Run(IEnumerable<string> folders, RecordingList list)
    {
        var results = new List<RecoveryResult>();
        foreach (var folder in folders)
        {
            foreach (var marker in RecordingRecovery.FindMarkers(folder))
            {
                var result = RecordingRecovery.Recover(marker);
                if (result.Outcome == RecoveryOutcome.Recovered)
                    list.AddRecovered(result);
                results.Add(result);
            }
        }
        return results;
    }
}
