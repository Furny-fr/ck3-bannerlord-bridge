// Opt-in stat normalization (bridge/data/normalization.json): each men-at-arms group's Bannerlord damage and health follow
// its CK3 base stats (ck3_catalog.json maa_types[].damage/toughness) relative to a baseline regiment. The factors multiply
// the group buffs of the knights pass; levies and knight retinues are left alone. Off (the default), the group buffs are
// returned untouched.
using System.Text.Json;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Planning;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Buffs;

/// <summary>bridge/data/normalization.json.</summary>
public sealed class NormalizationSettings
{
    public static readonly NormalizationSettings Off = new();

    public string NormalizationMode { get; set; } = "off";
    public double DamageScale { get; set; } = 1;
    public double ToughnessScale { get; set; } = 1;
    public double ClampMin { get; set; } = 0.5;
    public double ClampMax { get; set; } = 2.0;
    public double BaselineDamage { get; set; } = 30;
    public double BaselineToughness { get; set; } = 20;

    /// <summary>Mode "on" with usable values: positive finite baselines and bounds (min &lt;= max), non-negative finite scales.</summary>
    public bool IsOn =>
        string.Equals(NormalizationMode?.Trim(), "on", StringComparison.OrdinalIgnoreCase) &&
        Positive(BaselineDamage) && Positive(BaselineToughness) && Positive(ClampMin) && Positive(ClampMax) && ClampMin <= ClampMax &&
        double.IsFinite(DamageScale) && DamageScale >= 0 && double.IsFinite(ToughnessScale) && ToughnessScale >= 0;

    private static bool Positive(double v) => double.IsFinite(v) && v > 0;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>normalization.json of <paramref name="directory"/>; <see cref="Off"/> when the file is missing.</summary>
    public static NormalizationSettings Load(string directory)
    {
        string path = Path.Combine(directory, "normalization.json");
        if (!File.Exists(path)) return Off;
        return JsonSerializer.Deserialize<NormalizationSettings>(DataFile.ReadAllBytes(path), Json) ?? throw new InvalidDataException($"{path} is empty");
    }

    /// <summary>One line for bridge.log.</summary>
    public string Describe() => IsOn
        ? FormattableString.Invariant($"on (damage x{DamageScale} / {BaselineDamage}, toughness x{ToughnessScale} / {BaselineToughness}, clamp {ClampMin}..{ClampMax})")
        : string.Equals(NormalizationMode?.Trim(), "on", StringComparison.OrdinalIgnoreCase) ? "off (invalid values)" : "off";
}

public static class StatNormalizer
{
    /// <summary>
    /// The group buffs of one side with the normalization factors of its MaA groups multiplied in. Off: <paramref name="buffs"/>
    /// itself, unchanged. On: a new dictionary; a MaA group without buffs gets one, the others' entries are copies.
    /// </summary>
    public static Dictionary<string, GroupBuffs> Apply(Dictionary<string, GroupBuffs> buffs, IEnumerable<string> groupIds,
        NormalizationSettings settings, Ck3CatalogJson ck3)
    {
        if (!settings.IsOn) return buffs;
        var result = new Dictionary<string, GroupBuffs>(buffs, StringComparer.Ordinal);
        foreach (var id in groupIds.Distinct(StringComparer.Ordinal))
        {
            if (!GroupKey.TryParse(id, out var key) || key.Kind != GroupKind.Maa) continue;
            if (ck3.MaaTypes.GetValueOrDefault(key.MaaKey!) is not { } maa) continue;
            double dealt = Factor(settings.DamageScale, maa.Damage, settings.BaselineDamage, settings);
            double health = Factor(settings.ToughnessScale, maa.Toughness, settings.BaselineToughness, settings);
            var old = buffs.GetValueOrDefault(id);
            var b = new GroupBuffs
            {
                DamageDealtMult = (float)Clamp((old?.DamageDealtMult ?? 1) * dealt, settings),
                DamageTakenMult = old?.DamageTakenMult ?? 1,
                HealthMult = (float)Clamp((old?.HealthMult ?? 1) * health, settings),
                InitialMoraleAdd = old?.InitialMoraleAdd ?? 0,
            };
            if (b.DamageDealtMult != 1 || b.HealthMult != 1 || b.DamageTakenMult != 1 || b.InitialMoraleAdd != 0) result[id] = b;
            else result.Remove(id);
        }
        return result;
    }

    /// <summary>clamp(scale x stat / baseline); a stat of 0 or less (siege weapons) is neutral.</summary>
    public static double Factor(double scale, double stat, double baseline, NormalizationSettings settings) =>
        double.IsFinite(stat) && stat > 0 ? Clamp(scale * stat / baseline, settings) : 1;

    private static double Clamp(double v, NormalizationSettings s) => Math.Clamp(v, s.ClampMin, s.ClampMax);
}
