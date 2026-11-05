using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Compressor/limitador de cada reproductor (fase 4, pas 4b; D-067; protocol 4.13).
/// </summary>
public sealed partial class CommandRouter
{
    /// <summary>Presets que ofereix la interfície (de moment, fixos; els de l'usuari arribaran més endavant).</summary>
    private static readonly (string Id, string Name, DynamicsSettings Settings)[] ProcessingPresets =
    [
        ("sqrsoft-la-central", "SqrSoft La Central", DynamicsSettings.SqrSoftLaCentral),
        ("suau", "Compressió suau", new DynamicsSettings(true, InputGainDb: 3, ThresholdDb: -12, Ratio: 3, AttackMs: 10, ReleaseMs: 400)),
        ("nomes-sostre", "Només el sostre (sense compressió)", new DynamicsSettings(true, InputGainDb: 0, ThresholdDb: 0, Ratio: 1, AttackMs: 5, ReleaseMs: 500)),
    ];

    /// <summary>[{ id, name, processing }] per al camp processingPresets de settings.</summary>
    private static JsonArray ProcessingPresetsJson() => new(ProcessingPresets
        .Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["processing"] = ConfigRepository.WriteDynamics(null, p.Settings),
        }).ToArray());

    /// <summary>
    /// player; processing: { enabled?, inputGainDb?, thresholdDb?, ratio?, attackMs?, releaseMs?, outputGainDb?, stereoLink?, ceilingDb? }
    /// (els camps que no hi són es conserven; fora de marge, al límit) o preset (id d'un preset).
    /// S'aplica de seguida, també mentre sona, i es desa al perfil.
    /// </summary>
    private JsonObject PlayerSetProcessing(JsonObject msg)
    {
        var player = GetPlayer(msg);
        DynamicsSettings settings;
        if (ReadString(msg["preset"]) is { } presetId)
        {
            var preset = ProcessingPresets.FirstOrDefault(p => p.Id == presetId);
            if (preset.Id is null)
                throw new BadParamsException($"No hi ha cap preset «{presetId}».");
            settings = preset.Settings;
        }
        else if (msg["processing"] is JsonObject processing)
        {
            settings = ConfigRepository.ReadDynamics(processing, player.Dynamics);
        }
        else
        {
            throw new BadParamsException("Falta 'processing' (els ajustos) o 'preset'.");
        }

        player.SetDynamics(settings);
        _config?.Update(c => c.PlayerDynamics[player.Id] = player.Dynamics);
        return new JsonObject { ["player"] = PlayerJson.Player(player) };
    }
}
