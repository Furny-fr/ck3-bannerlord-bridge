// Port of tools/data/mapping_resolver.js (culture -> profile, ladders, MaA, levy, knights, commanders), function by function.
// Pure functions over MappingJson.
using CK3xBL.Mapping.Data;

namespace CK3xBL.Mapping.Resolution;

public sealed record ProfileResolution(string Profile, string BlCulture, string Via);

public sealed record MaaResolution(
    bool Excluded, string? Reason, double Tier, string? Role, string? LadderSource, IReadOnlyList<TroopWeight> Troops, double AgentMultiplier)
{
    public static MaaResolution Exclude(string? reason) => new(true, reason, 0, null, null, Array.Empty<TroopWeight>(), 0);
}

public sealed record LevyResolution(string Variant, IReadOnlyList<TroopWeight> Troops);

public sealed class TroopResolver
{
    public const string EarlyMedieval = "culture_era_early_medieval";

    private static readonly Dictionary<string, int> EraIndex = new(StringComparer.Ordinal)
    {
        ["culture_era_tribal"] = 0,
        [EarlyMedieval] = 1,
        ["culture_era_high_medieval"] = 2,
        ["culture_era_late_medieval"] = 3,
    };

    private readonly MappingJson _m;

    public TroopResolver(MappingJson mapping) => _m = mapping;

    public static bool IsKnownEra(string? era) => era is not null && EraIndex.ContainsKey(era);

    public static bool EraOk(IEraGated entry, string? era)
    {
        int e = era is not null && EraIndex.TryGetValue(era, out var i) ? i : 1;
        if (!string.IsNullOrEmpty(entry.MinEra) && EraIndex.TryGetValue(entry.MinEra, out var lo) && e < lo) return false;
        if (!string.IsNullOrEmpty(entry.MaxEra) && EraIndex.TryGetValue(entry.MaxEra, out var hi) && e > hi) return false;
        return true;
    }

    public static T? PickEra<T>(IEnumerable<T>? entries, string? era) where T : class, IEraGated =>
        entries?.FirstOrDefault(x => EraOk(x, era));

    // ---- culture -----------------------------------------------------------------------------------------------------

    /// <summary>Culture key, then (heritage|primary unit_gfx) exception, heritage, unit_gfx, default.</summary>
    public ProfileResolution ResolveProfile(string? culture, string? heritage, string? unitGfx)
    {
        string profile, via;
        if (!string.IsNullOrEmpty(culture) && _m.CultureProfileMap.TryGetValue(culture, out var p1)) { profile = p1; via = "culture"; }
        else if (!string.IsNullOrEmpty(heritage) && !string.IsNullOrEmpty(unitGfx) &&
                 _m.HeritageGfxOverrides.TryGetValue(heritage + "|" + unitGfx, out var p2)) { profile = p2.Profile; via = "pair"; }
        else if (!string.IsNullOrEmpty(heritage) && _m.HeritageProfileMap.TryGetValue(heritage, out var p3)) { profile = p3; via = "heritage"; }
        else if (!string.IsNullOrEmpty(unitGfx) && _m.UnitGfxProfileMap.TryGetValue(unitGfx, out var p4)) { profile = p4; via = "unit_gfx"; }
        else { profile = _m.DefaultProfile; via = "default"; }
        if (!_m.Profiles.ContainsKey(profile)) { profile = _m.DefaultProfile; via = "default"; }
        return new ProfileResolution(profile, ProfileOf(profile).BlCulture, via);
    }

    private Profile ProfileOf(string? profile) =>
        profile is not null && _m.Profiles.TryGetValue(profile, out var p) ? p : _m.Profiles[_m.DefaultProfile];

    // ---- ladders -----------------------------------------------------------------------------------------------------

    /// <summary>Splits a fractional tier between the two surrounding rungs; drops shares under 5%; weights rounded to 0.01.</summary>
    public static List<TroopWeight> Interpolate(IReadOnlyList<Rung> ladder, double t)
    {
        var rungs = ladder.OrderBy(r => r.Tier).ToList();
        var lo = rungs[0];
        var hi = rungs[^1];
        var mix = new List<(Rung Rung, double Weight)>();
        if (t <= lo.Tier) mix.Add((lo, 1));
        else if (t >= hi.Tier) mix.Add((hi, 1));
        else
        {
            Rung a = lo, b = hi;
            foreach (var r in rungs)
            {
                if (r.Tier <= t) a = r;
                if (r.Tier >= t) { b = r; break; }
            }
            if (ReferenceEquals(a, b)) mix.Add((a, 1));
            else
            {
                mix.Add((a, (b.Tier - t) / (b.Tier - a.Tier)));
                mix.Add((b, (t - a.Tier) / (b.Tier - a.Tier)));
            }
        }

        var acc = new List<KeyValuePair<string, double>>();     // insertion order, like the JS object accumulator
        foreach (var (rung, w) in mix)
        {
            foreach (var x in rung.Troops)
            {
                int i = acc.FindIndex(kv => kv.Key == x.Troop);
                if (i < 0) acc.Add(new(x.Troop, w * x.Weight));
                else acc[i] = new(x.Troop, acc[i].Value + w * x.Weight);
            }
        }
        var kept = acc.Where(kv => kv.Value >= 0.05).ToList();
        double s = kept.Sum(kv => kv.Value);
        return kept.Select(kv => new TroopWeight { Troop = kv.Key, Weight = JsCompat.R2(kv.Value / s) })
                   .OrderByDescending(x => x.Weight)
                   .ToList();
    }

    /// <summary>Extra agents when the tier overflows the ladder top (x2 cap), fewer below the bottom rung (x0.5 floor).</summary>
    public static double AgentMultiplier(IReadOnlyList<Rung> ladder, double t)
    {
        double lo = ladder.Min(r => r.Tier), hi = ladder.Max(r => r.Tier);
        if (t > hi) return JsCompat.R2(Math.Min(2, Math.Pow(2, (t - hi) / 2)));
        if (t < lo) return JsCompat.R2(Math.Max(0.5, Math.Pow(2, (t - lo) / 2)));
        return 1;
    }

    // ---- MaA ---------------------------------------------------------------------------------------------------------

    /// <param name="key">CK3 MaA type key.</param>
    /// <param name="profile">Profile of the regiment's culture.</param>
    /// <param name="era">Culture era key.</param>
    /// <param name="terrain">CK3 terrain of the battle (terrain tier delta), or null.</param>
    /// <param name="winterModifier">Province winter modifier key (winter_normal_modifier, ...) or MaA winter key, or null.</param>
    /// <param name="baseTypeHint">Base type for keys missing from mapping.json (modded MaA).</param>
    public MaaResolution ResolveMaa(string key, string profile, string? era, string? terrain, string? winterModifier, string? baseTypeHint)
    {
        _m.MaaTier.TryGetValue(key, out var info);
        string? baseType = info is not null ? info.BaseType : baseTypeHint;
        if (baseType is null || !_m.BaseRoles.TryGetValue(baseType, out var bas)) return MaaResolution.Exclude("unknown base type " + baseType);
        if (bas.Exclude || (info?.Excluded ?? false)) return MaaResolution.Exclude(bas.Note);

        _m.MaaOverrides.TryGetValue(key, out var ov);
        var prof = ProfileOf(profile);
        double tier = ov?.TierTarget ?? (info is not null ? info.TierTarget : bas.RefTier);
        var clamp = _m.TierRule.TerrainDeltaClamp;
        double dlo = clamp is { Count: >= 2 } ? clamp[0] : -1.5, dhi = clamp is { Count: >= 2 } ? clamp[1] : 1;
        double delta = 0;
        if (info?.TerrainTierDelta is not null && terrain is not null && info.TerrainTierDelta.TryGetValue(terrain, out var td)) delta += td;
        string? wk = winterModifier is null ? null : _m.WinterState.MaaWinterKey.GetValueOrDefault(winterModifier, winterModifier);
        if (info?.WinterTierDelta is not null && wk is not null && info.WinterTierDelta.TryGetValue(wk, out var wd)) delta += wd;
        tier = JsCompat.R2(Math.Max(0.5, tier + Math.Min(dhi, Math.Max(dlo, delta))));

        string? role = !string.IsNullOrEmpty(ov?.LadderRole) ? ov!.LadderRole : bas.Role;
        List<Rung>? ladder = null;
        string? source = null;
        string? reference = !string.IsNullOrEmpty(ov?.LadderRef) ? ov!.LadderRef : string.IsNullOrEmpty(ov?.LadderRole) ? bas.LadderRef : null;
        if (!string.IsNullOrEmpty(reference))
        {
            _m.SharedLadders.TryGetValue(reference, out ladder);
            source = "shared:" + reference;
        }
        else if (!string.IsNullOrEmpty(ov?.ForceCulture) || !string.IsNullOrEmpty(bas.ForceCulture))
        {
            string c = !string.IsNullOrEmpty(ov?.ForceCulture) ? ov!.ForceCulture! : bas.ForceCulture!;
            ladder = Ladder(role, c);
            source = $"{role}:{c} (forced)";
        }
        else
        {
            var po = role is not null && prof.RoleOverrides is not null && prof.RoleOverrides.TryGetValue(role, out var entries) ? PickEra(entries, era) : null;
            if (po?.Ladder is { Count: > 0 }) { ladder = po.Ladder; source = $"profile:{profile}:{role}"; }
            else if (!string.IsNullOrEmpty(po?.UseCulture)) { ladder = Ladder(role, po.UseCulture); source = $"{role}:{po.UseCulture} (profile)"; }
            else { ladder = Ladder(role, prof.BlCulture); source = $"{role}:{prof.BlCulture}"; }
        }
        if (ladder is not { Count: > 0 }) return MaaResolution.Exclude($"no ladder for {role}/{source}");
        return new MaaResolution(false, null, tier, role, source, Interpolate(ladder, tier), AgentMultiplier(ladder, tier));
    }

    private List<Rung>? Ladder(string? role, string culture) =>
        role is not null && _m.MaaLadders.TryGetValue(role, out var byCulture) && byCulture.TryGetValue(culture, out var l) ? l : null;

    // ---- levy / knights / commanders ---------------------------------------------------------------------------------

    public LevyResolution ResolveLevy(string profile, string? era, bool nomadic)
    {
        var prof = ProfileOf(profile);
        string variant = !string.IsNullOrEmpty(prof.LevyVariant) ? prof.LevyVariant : prof.BlCulture;
        if (nomadic && prof.BlCulture == "khuzait") variant = "steppe_nomadic";
        string e = era is not null && _m.LevyByEra.ContainsKey(era) ? era : EarlyMedieval;
        var byVariant = _m.LevyByEra[e];
        if (!byVariant.TryGetValue(variant, out var troops)) troops = byVariant[prof.BlCulture];
        return new LevyResolution(variant, troops);
    }

    public IReadOnlyList<TroopWeight> ResolveKnights(string profile, string? era)
    {
        var prof = ProfileOf(profile);
        var pk = PickEra(prof.Knights, era);
        return pk is not null ? pk.Troops : _m.Knights[prof.BlCulture];
    }

    /// <summary>Retinue agents of one knight: knight power 2.236 x prowess levy-equivalents, one elite agent ~ 4 levy agents, max 10.</summary>
    public static int KnightRetinue(double prowess, double scale, double effectivenessMult = 0) =>
        (int)Math.Min(10, JsCompat.Round(Math.Max(0, (2.236 * prowess * scale * (1 + effectivenessMult) - 4) / 4)));

    /// <summary>Stock custom-battle commander body; <paramref name="index"/> rotates bodies of the same culture.</summary>
    public string PickCommander(string blCulture, bool female, bool mounted = true, int index = 0)
    {
        var c = _m.Commanders.GetValueOrDefault(blCulture) ?? _m.Commanders[_m.DefaultCulture];
        string Take(List<CommanderBody> arr) => arr[((index % arr.Count) + arr.Count) % arr.Count].Character;
        if (female)
        {
            if (c.Female.Count > 0) return Take(c.Female);
            string fbCulture = _m.CommanderRules.FemaleFallback.GetValueOrDefault(blCulture) ?? _m.DefaultCulture;
            if (_m.Commanders.TryGetValue(fbCulture, out var fb) && fb.Female.Count > 0) return Take(fb.Female);
        }
        var slots = mounted ? new[] { c.MaleMounted, c.MaleFoot } : new[] { c.MaleFoot, c.MaleMounted };
        foreach (var s in slots) if (s.Count > 0) return Take(s);
        return Take(_m.Commanders[_m.DefaultCulture].MaleMounted);
    }
}
