using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Compressor/limitador de cada reproductor (fase 4, pas 4b; D-067; protocol 4.13) i els seus presets:
/// els de l'aplicació (fixos) i els propis, desats al perfil (D-069).
/// </summary>
public sealed partial class CommandRouter
{
    /// <summary>Presets de l'aplicació: no es poden esborrar ni sobreescriure.</summary>
    private static readonly ProcessingPreset[] BuiltInPresets =
    [
        new("sqrsoft-la-central", "SqrSoft La Central", DynamicsSettings.SqrSoftLaCentral),
        new("suau", "Compressió suau", new DynamicsSettings(true, InputGainDb: 3, ThresholdDb: -12, Ratio: 3, AttackMs: 10, ReleaseMs: 400)),
        new("nomes-sostre", "Només el sostre (sense compressió)", new DynamicsSettings(true, InputGainDb: 0, ThresholdDb: 0, Ratio: 1, AttackMs: 5, ReleaseMs: 500)),
    ];

    /// <summary>Tots els presets: primer els de l'aplicació i després els propis del perfil.</summary>
    private List<(ProcessingPreset Preset, bool BuiltIn)> AllPresets()
    {
        var list = BuiltInPresets.Select(p => (p, true)).ToList();
        if (_config is not null)
            list.AddRange(_config.Read(c => c.ProcessingPresets.ToList()).Select(p => (p, false)));
        return list;
    }

    /// <summary>[{ id, name, builtIn, processing }] per al camp processingPresets de settings.</summary>
    private JsonArray ProcessingPresetsJson() => new(AllPresets()
        .Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Preset.Id,
            ["name"] = p.Preset.Name,
            ["builtIn"] = p.BuiltIn,
            ["processing"] = ConfigRepository.WriteDynamics(null, p.Preset.Settings),
        }).ToArray());

    /// <summary>
    /// player; processing: { enabled?, inputGainDb?, thresholdDb?, ratio?, attackMs?, releaseMs?, outputGainDb?, stereoLink?, ceilingDb? }
    /// (els camps que no hi són es conserven; fora de marge, al límit) o preset (id d'un preset, de l'aplicació o propi).
    /// S'aplica de seguida, també mentre sona, i es desa al perfil.
    /// </summary>
    private JsonObject PlayerSetProcessing(JsonObject msg)
    {
        var player = GetPlayer(msg);
        DynamicsSettings settings;
        if (ReadString(msg["preset"]) is { } presetId)
        {
            var preset = AllPresets().FirstOrDefault(p => p.Preset.Id == presetId).Preset
                ?? throw new BadParamsException($"No hi ha cap preset «{presetId}».");
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

    /// <summary>
    /// Normalització de sonoritat d'un reproductor (fase 4, pas 5b; D-071): player, enabled.
    /// S'aplica a partir de la pista següent (la que sona no canvia de volum). Es desa al perfil.
    /// </summary>
    private JsonObject PlayerSetNormalization(JsonObject msg)
    {
        var player = GetPlayer(msg);
        if (msg["enabled"] is not JsonValue v || !v.TryGetValue(out bool enabled))
            throw new BadParamsException("'enabled' ha de ser true o false.");

        double target = _config?.Read(c => c.LoudnessTargetLufs) ?? EngineConfig.DefaultLoudnessTargetLufs;
        player.SetNormalization(enabled, target);
        _config?.Update(c =>
        {
            if (enabled) c.NormalizedPlayers.Add(player.Id);
            else c.NormalizedPlayers.Remove(player.Id);
        });
        return new JsonObject { ["player"] = PlayerJson.Player(player) };
    }

    /// <summary>
    /// Desa un preset propi: name, i els ajustos (processing) o els del reproductor (player).
    /// Si ja n'hi ha un de propi amb aquest nom, se substitueix (la interfície ho confirma abans).
    /// { preset: { id, name }, processingPresets }
    /// </summary>
    private JsonObject ProcessingSavePreset(JsonObject msg)
    {
        var config = RequireConfig();
        string name = ConfigRepository.CleanProgramName(ReadString(msg["name"]) ?? "", cut: false);
        if (name.Length == 0)
            throw new BadParamsException("El preset ha de tenir un nom.");
        if (name.Length > EngineConfig.MaxProcessingPresetName)
            throw new BadParamsException($"El nom d'un preset pot tenir com a molt {EngineConfig.MaxProcessingPresetName} caràcters.");
        if (BuiltInPresets.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new BadParamsException($"«{name}» és un preset de l'aplicació. Tria un altre nom.");

        DynamicsSettings settings = msg["processing"] is JsonObject processing
            ? ConfigRepository.ReadDynamics(processing)
            : msg["player"] is not null
                ? GetPlayer(msg).Dynamics
                : throw new BadParamsException("Falta 'processing' (els ajustos) o 'player'.");

        ProcessingPreset? saved = null;
        string? problem = null;
        config.Update(c =>
        {
            int index = c.ProcessingPresets.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (index >= 0)
            {
                saved = c.ProcessingPresets[index] with { Name = name, Settings = settings };
                c.ProcessingPresets[index] = saved;
            }
            else if (c.ProcessingPresets.Count >= EngineConfig.MaxProcessingPresets)
            {
                problem = $"Com a molt hi pot haver {EngineConfig.MaxProcessingPresets} presets propis. Esborra'n algun.";
            }
            else
            {
                saved = new ProcessingPreset("propi-" + Guid.NewGuid().ToString("N")[..8], name, settings);
                c.ProcessingPresets.Add(saved);
            }
        });
        if (problem is not null)
            throw new BadParamsException(problem);

        return new JsonObject
        {
            ["preset"] = new JsonObject { ["id"] = saved!.Id, ["name"] = saved.Name },
            ["processingPresets"] = ProcessingPresetsJson(),
        };
    }

    /// <summary>Esborra un preset propi: preset (id). Els de l'aplicació no es poden esborrar. { processingPresets }</summary>
    private JsonObject ProcessingDeletePreset(JsonObject msg)
    {
        var config = RequireConfig();
        string id = RequireString(msg, "preset");
        if (BuiltInPresets.Any(p => p.Id == id))
            throw new BadParamsException("Els presets de l'aplicació no es poden esborrar.");

        bool removed = false;
        config.Update(c => removed = c.ProcessingPresets.RemoveAll(p => p.Id == id) > 0);
        if (!removed)
            throw new BadParamsException($"No hi ha cap preset «{id}».");

        return new JsonObject { ["processingPresets"] = ProcessingPresetsJson() };
    }
}
