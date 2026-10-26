// Self tests of the opt-in stat normalization (StatNormalizer, bridge/data/normalization.json): off leaves the request
// untouched, on scales MaA groups by their CK3 base stats relative to the baseline, within the clamp bounds.
// Run: dotnet run --project CK3xBL.SelfTest -- mapping.normalization
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Config;
using CK3xBL.Mapping.Buffs;
using CK3xBL.Mapping.Data;
using CK3xBL.Protocol;

namespace CK3xBL.SelfTest;

internal static partial class MappingTests
{
    private static void RegisterNormalization(TestRegistry r)
    {
        r.Add("mapping.normalization.off_leaves_group_buffs_untouched", NormalizationOff);
        r.Add("mapping.normalization.on_scales_by_ck3_stats", NormalizationOn);
        r.Add("mapping.normalization.bounds_and_invalid_settings", NormalizationBounds);
        r.Add("mapping.normalization.request_end_to_end", NormalizationEndToEnd);
    }

    private static Ck3CatalogJson NormalizationCatalog() => new()
    {
        MaaTypes =
        {
            ["strong"] = new Ck3Maa { BaseType = "heavy_infantry", Damage = 24, Toughness = 20 },
            ["weak"] = new Ck3Maa { BaseType = "skirmishers", Damage = 12, Toughness = 10 },
            ["siege"] = new Ck3Maa { BaseType = "siege_weapon", Damage = 0, Toughness = 12 },
        },
    };

    private static NormalizationSettings NormalizationOn(double baselineDamage, double baselineToughness) => new()
    {
        NormalizationMode = "on", BaselineDamage = baselineDamage, BaselineToughness = baselineToughness,
    };

    private static readonly string[] NormalizationGroups =
        { "maa:strong#1#10", "maa:weak#1#11", "maa:siege#1#12", "maa:unknown#1#13", "levy#1", "knight-retinue#2" };

    private static void NormalizationOff()
    {
        // The shipped file is off, and off hands back the knights pass's dictionary itself.
        Expect.Equal("off", Tables.Normalization.NormalizationMode);
        Expect.False(Tables.Normalization.IsOn);
        Expect.Equal("off", Tables.Normalization.Describe());
        var buffs = new Dictionary<string, GroupBuffs> { ["maa:strong#1#10"] = new GroupBuffs { DamageDealtMult = 1.2f, HealthMult = 1.1f } };
        foreach (var off in new[] { Tables.Normalization, NormalizationSettings.Off, new NormalizationSettings { BaselineDamage = 6, BaselineToughness = 6 } })
        {
            var result = StatNormalizer.Apply(buffs, NormalizationGroups, off, NormalizationCatalog());
            Expect.True(ReferenceEquals(buffs, result), "off returns the same dictionary");
            Expect.Equal(1, result.Count);
            Expect.Equal(1.2f, result["maa:strong#1#10"].DamageDealtMult);
            Expect.Equal(1.1f, result["maa:strong#1#10"].HealthMult);
        }
    }

    private static void NormalizationOn()
    {
        var settings = NormalizationOn(18, 15);
        var empty = new Dictionary<string, GroupBuffs>();
        var result = StatNormalizer.Apply(empty, NormalizationGroups, settings, NormalizationCatalog());
        Expect.Equal(0, empty.Count, "the input dictionary is not modified");
        var strong = result["maa:strong#1#10"];
        var weak = result["maa:weak#1#11"];
        Expect.Near(24.0 / 18, strong.DamageDealtMult, 1e-6);
        Expect.Near(20.0 / 15, strong.HealthMult, 1e-6);
        Expect.Near(12.0 / 18, weak.DamageDealtMult, 1e-6);
        Expect.Near(10.0 / 15, weak.HealthMult, 1e-6);
        // 24 damage against 12: twice the multiplier, whatever the baseline.
        Expect.Near(2, strong.DamageDealtMult / weak.DamageDealtMult, 1e-5);
        Expect.Near(2, strong.HealthMult / weak.HealthMult, 1e-5);
        Expect.Equal(1f, strong.DamageTakenMult);
        Expect.Equal(0f, strong.InitialMoraleAdd);
        Expect.Near(2, StatNormalizer.Apply(empty, NormalizationGroups, NormalizationOn(20, 20), NormalizationCatalog())["maa:strong#1#10"].DamageDealtMult /
                       StatNormalizer.Apply(empty, NormalizationGroups, NormalizationOn(20, 20), NormalizationCatalog())["maa:weak#1#11"].DamageDealtMult, 1e-5,
            "another baseline, same ratio");

        // Siege weapons (damage 0) keep a neutral damage factor; unknown MaA, levies and retinues are left alone.
        var siege = result["maa:siege#1#12"];
        Expect.Equal(1f, siege.DamageDealtMult);
        Expect.Near(12.0 / 15, siege.HealthMult, 1e-6);
        Expect.False(result.ContainsKey("maa:unknown#1#13"));
        Expect.False(result.ContainsKey("levy#1"));
        Expect.False(result.ContainsKey("knight-retinue#2"));

        // Existing buffs are multiplied, not overwritten; their other members are kept.
        var knights = new Dictionary<string, GroupBuffs>
        {
            ["maa:strong#1#10"] = new GroupBuffs { DamageDealtMult = 1.2f, HealthMult = 0.9f, InitialMoraleAdd = 4 },
            ["levy#1"] = new GroupBuffs { DamageDealtMult = 1.1f },
        };
        result = StatNormalizer.Apply(knights, NormalizationGroups, settings, NormalizationCatalog());
        Expect.Near(1.2 * 24 / 18, result["maa:strong#1#10"].DamageDealtMult, 1e-5);
        Expect.Near(0.9 * 20 / 15, result["maa:strong#1#10"].HealthMult, 1e-5);
        Expect.Equal(4f, result["maa:strong#1#10"].InitialMoraleAdd);
        Expect.Equal(1.1f, result["levy#1"].DamageDealtMult, "levy buffs pass through");
        Expect.Equal(1.2f, knights["maa:strong#1#10"].DamageDealtMult, "the knights pass's entry is not modified");

        // Scales multiply the ratio.
        var scaled = new NormalizationSettings { NormalizationMode = "On", BaselineDamage = 18, BaselineToughness = 15, DamageScale = 0.5, ToughnessScale = 1.25 };
        result = StatNormalizer.Apply(empty, NormalizationGroups, scaled, NormalizationCatalog());
        Expect.Near(0.5 * 24 / 18, result["maa:strong#1#10"].DamageDealtMult, 1e-6);
        Expect.Near(1.25 * 10 / 15, result["maa:weak#1#11"].HealthMult, 1e-6);
    }

    private static void NormalizationBounds()
    {
        var empty = new Dictionary<string, GroupBuffs>();
        // A low baseline saturates at clamp_max, a high one at clamp_min.
        var high = StatNormalizer.Apply(empty, NormalizationGroups, NormalizationOn(6, 5), NormalizationCatalog());
        Expect.Equal(2f, high["maa:strong#1#10"].DamageDealtMult);
        Expect.Equal(2f, high["maa:weak#1#11"].HealthMult);
        var low = StatNormalizer.Apply(empty, NormalizationGroups, NormalizationOn(100, 100), NormalizationCatalog());
        Expect.Equal(0.5f, low["maa:strong#1#10"].DamageDealtMult);
        Expect.Equal(0.5f, low["maa:weak#1#11"].HealthMult);

        // The product with an existing buff stays within the bounds too.
        var knights = new Dictionary<string, GroupBuffs> { ["maa:strong#1#10"] = new GroupBuffs { DamageDealtMult = 1.5f, HealthMult = 0.6f } };
        var both = StatNormalizer.Apply(knights, NormalizationGroups, NormalizationOn(12, 40), NormalizationCatalog());
        Expect.Equal(2f, both["maa:strong#1#10"].DamageDealtMult, "1.5 x 2 clamped");
        Expect.Equal(0.5f, both["maa:strong#1#10"].HealthMult, "0.6 x 0.5 clamped");

        // Custom bounds.
        var narrow = new NormalizationSettings { NormalizationMode = "on", BaselineDamage = 6, BaselineToughness = 100, ClampMin = 0.8, ClampMax = 1.25 };
        var n = StatNormalizer.Apply(empty, NormalizationGroups, narrow, NormalizationCatalog());
        Expect.Equal(1.25f, n["maa:strong#1#10"].DamageDealtMult);
        Expect.Equal(0.8f, n["maa:strong#1#10"].HealthMult);

        // Unusable values turn the mode off instead of producing NaN or inverted bounds.
        foreach (var bad in new[]
                 {
                     new NormalizationSettings { NormalizationMode = "on", BaselineDamage = 0 },
                     new NormalizationSettings { NormalizationMode = "on", BaselineToughness = double.NaN },
                     new NormalizationSettings { NormalizationMode = "on", ClampMin = 2, ClampMax = 0.5 },
                     new NormalizationSettings { NormalizationMode = "on", ClampMin = 0 },
                     new NormalizationSettings { NormalizationMode = "on", DamageScale = -1 },
                     new NormalizationSettings { NormalizationMode = "on", ToughnessScale = double.PositiveInfinity },
                     new NormalizationSettings { NormalizationMode = "yes" },
                 })
        {
            Expect.False(bad.IsOn);
            Expect.True(ReferenceEquals(empty, StatNormalizer.Apply(empty, NormalizationGroups, bad, NormalizationCatalog())));
        }
        Expect.Equal("off (invalid values)", new NormalizationSettings { NormalizationMode = "on", BaselineDamage = 0 }.Describe());
        Expect.Equal("on (damage x1 / 30, toughness x1 / 20, clamp 0.5..2)", new NormalizationSettings { NormalizationMode = "on" }.Describe());
    }

    /// <summary>The whole mapper with a data folder whose normalization.json is on (and one without the file): the knights
    /// battle's MaA groups are scaled from ck3_catalog.json, its levies and retinues keep the knights pass's buffs.</summary>
    private static void NormalizationEndToEnd()
    {
        string source = DataLocator.Resolve(null);
        string dir = Path.Combine(Path.GetTempPath(), "ck3xbl_normalization_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in Directory.GetFiles(source, "*.json"))
                if (Path.GetFileName(f) != "normalization.json") File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            var battle = KnightsBattle();
            var stock = Mapper.BuildRequest(battle, new MapperOptions());

            // No file: off, the request's group buffs are the stock ones.
            var missing = Mapper.BuildRequest(battle, new MapperOptions { DataDirectory = dir });
            Expect.False(MappingTables.Get(dir).Normalization.IsOn);
            foreach (var (a, b) in new[] { (stock.Attacker, missing.Attacker), (stock.Defender, missing.Defender) })
            {
                Expect.Equal(a.GroupBuffs.Count, b.GroupBuffs.Count);
                foreach (var (id, g) in a.GroupBuffs)
                {
                    Expect.Equal(g.DamageDealtMult, b.GroupBuffs[id].DamageDealtMult, id);
                    Expect.Equal(g.HealthMult, b.GroupBuffs[id].HealthMult, id);
                    Expect.Equal(g.InitialMoraleAdd, b.GroupBuffs[id].InitialMoraleAdd, id);
                }
            }

            string onDir = dir + "_on";
            Directory.CreateDirectory(onDir);
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.json")) File.Copy(f, Path.Combine(onDir, Path.GetFileName(f)));
                File.WriteAllText(Path.Combine(onDir, "normalization.json"),
                    "{ \"normalization_mode\": \"on\", \"damage_scale\": 1.0, \"toughness_scale\": 1.0, \"clamp_min\": 0.5, \"clamp_max\": 2.0, " +
                    "\"baseline_damage\": 30, \"baseline_toughness\": 20 }");
                var on = Mapper.BuildRequest(battle, new MapperOptions { DataDirectory = onDir });
                Expect.True(MappingTables.Get(onDir).Normalization.IsOn);
                var catalog = Tables.Ck3.MaaTypes;
                int maaGroups = 0;
                foreach (var (stockSide, onSide) in new[] { (stock.Attacker, on.Attacker), (stock.Defender, on.Defender) })
                {
                    foreach (var t in onSide.Troops.Where(t => t.Group is not null).DistinctBy(t => t.Group))
                    {
                        stockSide.GroupBuffs.TryGetValue(t.Group!, out var before);
                        onSide.GroupBuffs.TryGetValue(t.Group!, out var after);
                        if (!t.Group!.StartsWith("maa:", StringComparison.Ordinal))
                        {
                            Expect.Equal(before?.DamageDealtMult, after?.DamageDealtMult, t.Group);
                            Expect.Equal(before?.HealthMult, after?.HealthMult, t.Group);
                            continue;
                        }
                        maaGroups++;
                        var maa = catalog[t.Group[4..t.Group.IndexOf('#')]];
                        var a = Expect.NotNull(after, t.Group);
                        Expect.Near(Math.Clamp((before?.DamageDealtMult ?? 1) * Math.Clamp(maa.Damage / 30, 0.5, 2), 0.5, 2), a.DamageDealtMult, 1e-5, t.Group);
                        Expect.Near(Math.Clamp((before?.HealthMult ?? 1) * Math.Clamp(maa.Toughness / 20, 0.5, 2), 0.5, 2), a.HealthMult, 1e-5, t.Group);
                        Expect.Equal(before?.InitialMoraleAdd ?? 0, a.InitialMoraleAdd, t.Group);
                    }
                }
                Expect.True(maaGroups >= 2, "the knights battle has MaA groups");
            }
            finally
            {
                try { Directory.Delete(onDir, recursive: true); } catch (IOException) { }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
