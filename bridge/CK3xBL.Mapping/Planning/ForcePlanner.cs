// CK3 armies -> Bannerlord troop groups (PROTOCOL §6): culture per army (hired regiments keep their own culture),
// levies by era, each MaA regiment through the continuous tier rule, knight retinues, then the agent budget.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Planning;

public sealed record CultureChoice(string Profile, string BlCulture, string? Era, string Via);

public sealed class PlannedGroup
{
    public required GroupKey Key { get; init; }
    public string Id => Key.ToString();
    public required SideKey Side { get; init; }
    /// <summary>CK3 men the group stands for (0 for knight retinues).</summary>
    public required int Soldiers { get; init; }
    public required IReadOnlyList<TroopWeight> Troops { get; init; }
    public required string BlCulture { get; init; }
    public double Tier { get; init; }
    public double AgentMultiplier { get; init; } = 1;
    public string? LadderSource { get; init; }
    public int Agents { get; set; }
}

public sealed class SidePlan
{
    public required SideKey Key { get; init; }
    public required SideSnapshot Snapshot { get; init; }
    public List<PlannedGroup> Groups { get; } = new();
    public List<PlannedGroup> Retinues { get; } = new();
    public Dictionary<int, CultureChoice> ArmyCultures { get; } = new();
    public double Scale { get; set; }
    public bool Floored { get; set; }
    public int Soldiers => Groups.Sum(g => g.Soldiers);
    public int GroupAgents => Groups.Sum(g => g.Agents);
    public int TroopAgents => GroupAgents + Retinues.Sum(g => g.Agents);

    /// <summary>Bannerlord culture with the most CK3 men on this side.</summary>
    public string DominantCulture(string fallback) =>
        Groups.GroupBy(g => g.BlCulture).Select(g => (Culture: g.Key, Men: g.Sum(x => x.Soldiers)))
              .OrderByDescending(x => x.Men).ThenBy(x => x.Culture, StringComparer.Ordinal).Select(x => x.Culture).FirstOrDefault() ?? fallback;
}

/// <summary>CK3 location facts the mapper uses, read from the typed snapshot fields and the raw L|field dictionary.</summary>
public sealed record LocationFacts
{
    public string? Terrain { get; init; }
    public string? Winter { get; init; }
    public string Holding { get; init; } = "none";
    /// <summary>The holding here is besieged (L|siege): a relief battle. Field battle all the same (PROTOCOL §0).</summary>
    public bool SiegeHere { get; init; }
    public bool Coastal { get; init; }
    public bool Riverside { get; init; }
    public string? Region { get; init; }
    public string? BuildingGfx { get; init; }
    public string? Heritage { get; init; }
    public string? Culture { get; init; }

    public bool HarshWinter => Winter == "harsh";

    /// <summary>Province winter modifier key (winter_normal_modifier, ...), or null when there is no winter.</summary>
    public string? WinterModifier => Winter is null or "none" ? null : $"winter_{Winter}_modifier";

    public static LocationFacts From(BattleSnapshot battle, MappingTables t)
    {
        var loc = battle.Location;
        var facts = loc.Facts ?? new Dictionary<string, string>();
        string? Raw(string key) => facts.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        // A typed field equal to its parser default is only trusted when the L record was seen (or no raw facts were kept at all).
        string? Logged(string key, string typed, string typedDefault) => Raw(key) ?? (facts.Count == 0 || typed != typedDefault ? typed : null);
        bool Flag(string key, bool typed) => typed || Raw(key) is "yes" or "1";

        string? culture = Clean(Raw("loc_culture"));
        var cat = culture is not null ? t.Ck3.Cultures.GetValueOrDefault(culture) : null;
        return new LocationFacts
        {
            Terrain = Clean(Logged("terrain", loc.Terrain, "plains")),
            Winter = Logged("winter", loc.Winter, "none"),          // "none" is a logged fact (no winter); null = not logged
            Holding = Clean(loc.Holding) ?? Clean(Raw("holding")) ?? "none",
            SiegeHere = Flag("siege", loc.Siege),
            Coastal = Flag("coastal", loc.Coastal),
            Riverside = Flag("riverside", loc.Riverside),
            Region = Clean(Raw("region")),
            BuildingGfx = Clean(Raw("building_gfx")) ?? cat?.BuildingGfx.FirstOrDefault(),
            Heritage = Clean(Raw("loc_heritage")) ?? cat?.Heritage,
            Culture = culture,
        };
    }

    /// <summary>Null for absent, empty, "none" and "unknown" values.</summary>
    public static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) || v is "none" or "unknown" ? null : v.Trim();
}

public sealed class ForcePlanner
{
    private const string UnknownMaaBaseType = "heavy_infantry";   // unknown keys use a base type's reference ladder
    private const string NomadHerdMaa = "nomadic_riders";          // only nomadic governments field herd riders

    private readonly MappingTables _t;
    private readonly TroopResolver _troops;

    public ForcePlanner(MappingTables tables, TroopResolver troops)
    {
        _t = tables;
        _troops = troops;
    }

    public (SidePlan Attacker, SidePlan Defender) Plan(BattleSnapshot battle, LocationFacts facts, MapperOptions options)
    {
        var sides = new[] { BuildSide(battle, battle.Attacker, facts), BuildSide(battle, battle.Defender, facts) };

        // Retinues are sized from the scale the battle would have without them, then come out of the same agent budget.
        double s0 = AgentAllocator.BaseScale(options.MaxAgentsTotal, sides.Sum(s => s.Soldiers));
        foreach (var side in sides) AddRetinues(battle, side, s0);
        CapRetinues(sides, options.MaxAgentsTotal / 5);

        var alloc = sides.Select(s =>
        {
            var a = new AllocationSide();
            foreach (var g in s.Groups)
                a.Groups.Add(new AllocationGroup { Id = g.Id, IsLevy = g.Key.Kind == GroupKind.Levy, Soldiers = g.Soldiers, AgentMultiplier = g.AgentMultiplier });
            return a;
        }).ToList();
        int reserved = sides.Sum(s => s.Retinues.Sum(r => r.Agents));
        AgentAllocator.Allocate(alloc, options.MaxAgentsTotal, reserved, options.MinAgentsPerSide);
        for (int i = 0; i < 2; i++)
        {
            sides[i].Scale = alloc[i].Scale;
            sides[i].Floored = alloc[i].Floored;
            for (int g = 0; g < sides[i].Groups.Count; g++) sides[i].Groups[g].Agents = alloc[i].Groups[g].Agents;
        }
        return (sides[0], sides[1]);
    }

    /// <summary>The TroopRequests of a side: each group's agents split over its troop mix by largest remainder.</summary>
    public static List<TroopRequest> Troops(SidePlan side)
    {
        var list = new List<TroopRequest>();
        foreach (var g in side.Groups.Concat(side.Retinues))
            foreach (var (troop, count) in Split(g.Troops, g.Agents))
                list.Add(new TroopRequest { Id = troop, Count = count, Group = g.Id });
        return list;
    }

    private static IEnumerable<(string Troop, int Count)> Split(IReadOnlyList<TroopWeight> mix, int agents)
    {
        if (agents <= 0 || mix.Count == 0) yield break;
        double total = mix.Sum(x => Math.Max(0, x.Weight));
        if (total <= 0) { yield return (mix[0].Troop, agents); yield break; }
        var exact = mix.Select(x => Math.Max(0, x.Weight) / total * agents).ToList();
        var counts = exact.Select(e => (int)Math.Floor(e)).ToArray();
        int left = agents - counts.Sum();
        foreach (int i in Enumerable.Range(0, mix.Count).OrderByDescending(i => exact[i] - counts[i]).ThenBy(i => i).Take(left)) counts[i]++;
        for (int i = 0; i < mix.Count; i++) if (counts[i] > 0) yield return (mix[i].Troop, counts[i]);
    }

    // ---- per side ----------------------------------------------------------------------------------------------------

    private SidePlan BuildSide(BattleSnapshot battle, SideSnapshot side, LocationFacts facts)
    {
        var plan = new SidePlan { Key = side.Key, Snapshot = side };
        foreach (var army in side.Armies.OrderBy(a => a.Tag))
        {
            var culture = ArmyCulture(battle, side, army);
            plan.ArmyCultures[army.Tag] = culture;
            bool nomadic = army.Regiments.Any(r => r.MaaKey == NomadHerdMaa && r.Current > 0);

            if (army.Levies > 0)
            {
                var levy = _troops.ResolveLevy(culture.Profile, culture.Era, nomadic);
                plan.Groups.Add(new PlannedGroup
                {
                    Key = GroupKey.Levy(army.Tag), Side = side.Key, Soldiers = army.Levies, Troops = SpawnableOnly(levy.Troops),
                    BlCulture = culture.BlCulture, LadderSource = "levy:" + levy.Variant,
                });
            }

            // A regiment without a MaA key could not be written back (PROTOCOL §2.1 selects regiments by is_maa_type).
            foreach (var reg in army.Regiments.Where(r => r.Current > 0 && !string.IsNullOrEmpty(r.MaaKey)).OrderBy(r => r.RegimentId))
            {
                var regCulture = reg.Hired && HasCulture(reg.HiredCulture) ? Resolve(reg.HiredCulture!, culture.Era) : culture;
                string? baseHint = _t.Mapping.MaaTier.ContainsKey(reg.MaaKey) ? null
                    : _t.Ck3.MaaTypes.GetValueOrDefault(reg.MaaKey)?.BaseType ?? UnknownMaaBaseType;
                var maa = _troops.ResolveMaa(reg.MaaKey, regCulture.Profile, regCulture.Era, facts.Terrain, facts.WinterModifier, baseHint);
                if (maa.Excluded) continue;       // siege weapons never fight in a field battle (fights_in_main_phase = no)
                plan.Groups.Add(new PlannedGroup
                {
                    Key = GroupKey.Maa(reg.MaaKey, army.Tag, reg.RegimentId), Side = side.Key, Soldiers = reg.Current,
                    Troops = SpawnableOnly(maa.Troops), BlCulture = regCulture.BlCulture, Tier = maa.Tier,
                    AgentMultiplier = maa.AgentMultiplier, LadderSource = maa.LadderSource,
                });
            }
        }
        plan.Groups.RemoveAll(g => g.Troops.Count == 0);
        return plan;
    }

    private void AddRetinues(BattleSnapshot battle, SidePlan plan, double scale)
    {
        foreach (var c in plan.Snapshot.Characters.Where(c => c.IsKnight && !c.IsCommander && c.Tag > 0).OrderBy(c => c.Tag))
        {
            int n = TroopResolver.KnightRetinue(c.Prowess, scale);
            if (n <= 0) continue;
            var culture = CharacterCulture(battle, plan, c);
            plan.Retinues.Add(new PlannedGroup
            {
                Key = GroupKey.Retinue(c.Tag), Side = plan.Key, Soldiers = 0, Agents = n,
                Troops = SpawnableOnly(_troops.ResolveKnights(culture.Profile, culture.Era)), BlCulture = culture.BlCulture, LadderSource = "knights",
            });
        }
        plan.Retinues.RemoveAll(g => g.Troops.Count == 0);
    }

    // Retinues must never crowd out the armies: keep them under a fifth of the budget (only matters for tiny budgets).
    private static void CapRetinues(SidePlan[] sides, int cap)
    {
        var all = sides.SelectMany(s => s.Retinues).ToList();
        int total = all.Sum(r => r.Agents);
        if (total <= cap) return;
        double f = cap / (double)total;
        foreach (var r in all) r.Agents = (int)Math.Floor(r.Agents * f);
        foreach (var s in sides) s.Retinues.RemoveAll(r => r.Agents <= 0);
    }

    // ---- cultures ----------------------------------------------------------------------------------------------------

    /// <summary>The army owner's culture (AX lines); else the owner's or commander's own culture (CX lines).</summary>
    private CultureChoice ArmyCulture(BattleSnapshot battle, SideSnapshot side, ArmySnapshot army)
    {
        var owner = side.Characters.FirstOrDefault(c => army.Owner is not null && c.Ref.Ck3Id == army.Owner.Ck3Id);
        var commander = side.Characters.FirstOrDefault(c => army.Commander is not null && c.Ref.Ck3Id == army.Commander.Ck3Id);
        var source = new[] { army.OwnerCulture, owner?.Culture, commander?.Culture }.FirstOrDefault(HasCulture);
        string? era = Era(army.OwnerCulture) ?? Era(owner?.Culture) ?? Era(commander?.Culture)
                      ?? SameCultureEra(side, source) ?? EraFromYear(battle.Year);
        return source is not null ? Resolve(source, era) : DefaultCulture(era);
    }

    /// <summary>A character's own culture (CX lines), else the culture of the army they fight in, else the side's first army.</summary>
    public CultureChoice CharacterCulture(BattleSnapshot battle, SidePlan plan, CharacterSnapshot c)
    {
        CultureChoice? army = plan.ArmyCultures.GetValueOrDefault(c.ArmyTag) ?? plan.ArmyCultures.Values.FirstOrDefault();
        string? era = Era(c.Culture) ?? army?.Era ?? EraFromYear(battle.Year);
        if (HasCulture(c.Culture)) return Resolve(c.Culture, era);
        return army ?? DefaultCulture(era);
    }

    private CultureChoice Resolve(CultureInfo info, string? era)
    {
        var p = _troops.ResolveProfile(LocationFacts.Clean(info.Culture), LocationFacts.Clean(info.Heritage), LocationFacts.Clean(info.UnitGfx));
        return new CultureChoice(p.Profile, p.BlCulture, Era(info) ?? era, p.Via);
    }

    private CultureChoice DefaultCulture(string? era)
    {
        var p = _troops.ResolveProfile(null, null, null);
        return new CultureChoice(p.Profile, p.BlCulture, era, p.Via);
    }

    private static bool HasCulture(CultureInfo? c) =>
        c is not null && (LocationFacts.Clean(c.Culture) ?? LocationFacts.Clean(c.Heritage) ?? LocationFacts.Clean(c.UnitGfx)) is not null;

    private static string? Era(CultureInfo? c) => TroopResolver.IsKnownEra(c?.Era) ? c!.Era : null;

    private static string? SameCultureEra(SideSnapshot side, CultureInfo? culture)
    {
        string? key = LocationFacts.Clean(culture?.Culture);
        if (key is null) return null;
        return side.Characters.Where(c => c.Culture.Culture == key).Select(c => Era(c.Culture)).FirstOrDefault(e => e is not null);
    }

    /// <summary>INFERRED fallback when no record carries an era: eras open in 900/1050/1200 and spread over about 50 years.</summary>
    public static string EraFromYear(int year) => year switch
    {
        < 950 => "culture_era_tribal",
        < 1100 => TroopResolver.EarlyMedieval,
        < 1250 => "culture_era_high_medieval",
        _ => "culture_era_late_medieval",
    };

    private List<TroopWeight> SpawnableOnly(IReadOnlyList<TroopWeight> mix) => mix.Where(x => _t.IsSpawnableTroop(x.Troop)).ToList();
}
