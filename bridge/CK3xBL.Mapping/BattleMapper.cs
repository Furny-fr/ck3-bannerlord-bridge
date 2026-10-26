// CK3xBL.Mapping entry point: CK3 battle snapshot -> Bannerlord request (PROTOCOL §3.2), Bannerlord result -> CK3 outcome
// (PROTOCOL §2.1). Ports tools/data/mapping_resolver.js and tools/data/battlefield_resolver.js (PROTOCOL §6).
// Stateless apart from the shared, read-only data tables.
// The knights pass (PROTOCOL §13, KnightBuffPlanner) runs only for snapshots of a mod that logs the knights facts
// (BattleSnapshot.KnightFactsLogged): an older mod gets today's request and outcome member for member (PROTOCOL §13.1).
// Battle clock (PROTOCOL §14): outcomes are keypad payload 4 (kill targets, absolute levy
// survivors) unless MapperOptions.BattleClock says otherwise, and a reinforcement clash is planned from the live snapshot capped at
// the earlier clashes' end state (ContinuationSnapshot), on the earlier clash's field, with cumulative targets.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Buffs;
using CK3xBL.Mapping.Continuation;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Outcome;
using CK3xBL.Mapping.Planning;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping;

/// <summary>Everything decided for one battle; <see cref="Request"/> is what goes to Bannerlord, the rest explains it.</summary>
public sealed class BattlePlan
{
    public required BattleRequest Request { get; init; }
    public required BattlefieldChoice Battlefield { get; init; }
    public required LocationFacts Location { get; init; }
    public required SidePlan Attacker { get; init; }
    public required SidePlan Defender { get; init; }
    public required HeroAssignment Heroes { get; init; }
}

public sealed class BattleMapper : IBattleMapper
{
    public BattleRequest BuildRequest(BattleSnapshot battle, MapperOptions options) => Plan(battle, options).Request;

    public Ck3Outcome BuildOutcome(BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options) =>
        BuildOutcome(battle, request, result, options, null);

    /// <summary>The outcome of a clash; <paramref name="battle"/> is the snapshot the request was planned from (the capped one of a
    /// continuation), and <paramref name="parent"/> the clash it continues (targets and r4..r6 cumulative), or null.</summary>
    public Ck3Outcome BuildOutcome(BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options, ParentLink? parent)
    {
        var tables = MappingTables.Get(options.DataDirectory);
        var facts = EffectiveFacts(battle, tables);
        KnightBuffPlanner? knights = null;
        if (battle.KnightFactsLogged)
        {
            CharacterSnapshot? General(SideKey key)
            {
                var side = key == SideKey.Attacker ? request.Attacker : request.Defender;
                var spec = side?.Heroes.FirstOrDefault(h => h.Key == side.General);
                var c = spec is null ? null : battle.Side(key).Characters.FirstOrDefault(x => x.Ref.Ck3Id == spec.Ck3Id);
                // A stand-in player (PROTOCOL §15) leads in Bannerlord only: not the side's CK3 commander (HeroAssignment.Ck3Commander).
                return c is not null && HeroPlanner.IsPresent(c) ? c : null;
            }
            knights = new KnightBuffPlanner(tables, battle, facts, General(SideKey.Attacker), General(SideKey.Defender));
        }
        return new OutcomeBuilder(tables, battle, request, result, options, facts, knights, parent).Build();
    }

    /// <summary>
    /// Plans a reinforcement clash (PROTOCOL §14.6): the live snapshot capped at the earlier clashes' end state, fought on the
    /// earlier clash's field (scene, season, kind) at the next stock hour, with the earlier player side, coats of arms, staging and
    /// detail under the title "Reinforcements arrive!". The outcome of the clash is built from <see cref="ContinuationPlan.Capped"/>.
    /// </summary>
    public ContinuationPlan BuildContinuation(BattleSnapshot live, ParentLink parent, MapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var capped = ContinuationSnapshot.Cap(live, parent);
        var request = Plan(capped, options).Request;
        var earlier = parent.Request;
        request.Kind = earlier.Kind;
        request.Scene = earlier.Scene;
        request.Season = earlier.Season;
        request.TimeOfDay = NextStockHour(earlier.TimeOfDay);
        request.PlayerSide = earlier.PlayerSide;
        request.Presentation = PresentationWriter.Continuation(capped, parent, request.Presentation);
        return new ContinuationPlan { Request = request, Capped = capped };
    }

    private static readonly int[] DaytimeStockHours = { 6, 12, 15, 18 };

    /// <summary>The stock custom-battle hour after an earlier clash's: the day moves on but never into the night (6 -> 12 -> 15 -> 18;
    /// 18 and a night battle at 22 stay).</summary>
    private static float NextStockHour(float hour) => DaytimeStockHours.Where(h => h > hour).Select(h => (float)h).DefaultIfEmpty(hour).First();

    /// <summary>Resolves the whole battle (troops, heroes, battlefield, presentation). Deterministic for a given snapshot.</summary>
    public BattlePlan Plan(BattleSnapshot battle, MapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Plan(battle, options, MappingTables.Get(options.DataDirectory));
    }

    /// <summary>The same against given tables (MapperOptions.DataDirectory is not consulted).</summary>
    public BattlePlan Plan(BattleSnapshot battle, MapperOptions options, MappingTables tables)
    {
        ArgumentNullException.ThrowIfNull(battle);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tables);
        var troops = new TroopResolver(tables.Mapping);
        var forcePlanner = new ForcePlanner(tables, troops);
        var facts = EffectiveFacts(battle, tables);

        var (attacker, defender) = forcePlanner.Plan(battle, facts, options);
        var heroes = new HeroPlanner(tables, troops, forcePlanner).Plan(battle, attacker, defender);
        var knights = battle.KnightFactsLogged
            ? new KnightBuffPlanner(tables, battle, facts, heroes.Ck3Commander(SideKey.Attacker), heroes.Ck3Commander(SideKey.Defender))
            : null;
        knights?.ShapeHeroes(heroes, attacker, defender, forcePlanner);

        int agents = attacker.TroopAgents + defender.TroopAgents + heroes.Heroes.Count;
        var field = new BattlefieldResolver(tables).Resolve(new BattlefieldInput
        {
            ProvinceId = battle.Location.ProvinceId,
            Year = battle.Year, Month = battle.Month, Day = battle.Day, BattleSeq = battle.Bid,
            Terrain = facts.Terrain, Winter = facts.Winter,
            Holding = facts.Holding, BuildingGfx = facts.BuildingGfx ?? ReliefSideBuildingGfx(tables, attacker),
            Heritage = facts.Heritage, SiegeHere = facts.SiegeHere, Coastal = facts.Coastal, Riverside = facts.Riverside,
            ClimateRegion = facts.Region, Agents = agents,
        });

        int soldiers = attacker.Soldiers + defender.Soldiers;
        int groupAgents = attacker.GroupAgents + defender.GroupAgents;
        var playerPlan = heroes.PlayerSide == SideKey.Attacker ? attacker : defender;
        var request = new BattleRequest
        {
            Protocol = ProtocolInfo.Version,
            Id = battle.RequestId,
            Kind = "field",            // always: sieges stay in CK3 and Siege stays null (PROTOCOL §0)
            Scene = field.Scene,
            Season = field.Season,
            TimeOfDay = field.LegacyTimeOfDay,
            PlayerSide = heroes.PlayerSide == SideKey.Attacker ? "attacker" : "defender",
            PlayerIsGeneral = heroes.PlayerIsGeneral,
            Presentation = PresentationWriter.Build(battle, field, heroes, PresentationWriter.SideName(playerPlan.Snapshot), options),
            Scale = new ScaleInfo { MenPerAgent = (float)JsCompat.R2(soldiers / (double)Math.Max(1, groupAgents)), BattleSize = options.BattleSize },
            Attacker = SideRequestFor(tables, troops, attacker, heroes, knights),
            Defender = SideRequestFor(tables, troops, defender, heroes, knights),
        };
        return new BattlePlan
        {
            Request = request, Battlefield = field, Location = facts, Attacker = attacker, Defender = defender, Heroes = heroes,
        };
    }

    /// <summary>The logged facts with the terrain the battle is actually fought on (logged key, else the static index, else plains).</summary>
    internal static LocationFacts EffectiveFacts(BattleSnapshot battle, MappingTables tables)
    {
        var facts = LocationFacts.From(battle, tables);
        string? logged = facts.Terrain;
        string terrain = logged is not null && tables.Terrain.Terrain.ContainsKey(logged) ? logged
            : tables.Provinces.TryGetValue((int)battle.Location.ProvinceId, out var p) && tables.Terrain.Terrain.ContainsKey(p.Terrain) ? p.Terrain
            : "plains";
        return facts with { Terrain = terrain };
    }

    // Without a logged building_gfx the village's look comes from the relief army's culture: in a CK3 relief battle the army
    // that marches in (the combat attacker) comes to lift the siege of its own holding (INFERRED; the mod logs L|building_gfx).
    private static string? ReliefSideBuildingGfx(MappingTables tables, SidePlan attacker)
    {
        var culture = attacker.Snapshot.Armies.OrderByDescending(a => a.Size).Select(a => LocationFacts.Clean(a.OwnerCulture.Culture)).FirstOrDefault(c => c is not null);
        return culture is not null ? tables.Ck3.Cultures.GetValueOrDefault(culture)?.BuildingGfx.FirstOrDefault() : null;
    }

    private static SideRequest SideRequestFor(MappingTables tables, TroopResolver troops, SidePlan side, HeroAssignment heroes, KnightBuffPlanner? knights)
    {
        string culture = side.DominantCulture(tables.Mapping.DefaultCulture);
        var general = heroes.General(side.Key);
        var troopRequests = ForcePlanner.Troops(side);
        return new SideRequest
        {
            Name = PresentationWriter.SideName(side.Snapshot),
            Culture = culture,
            General = general?.Spec.Key ?? troops.PickCommander(culture, female: false, mounted: true, index: side.Key == SideKey.Attacker ? 0 : 1),
            Heroes = heroes.Heroes.Where(h => h.Side == side.Key).Select(h => h.Spec).ToList(),
            Troops = troopRequests,
            Buffs = knights?.SideBuffsFor(side.Key),
            GroupBuffs = StatNormalizer.Apply(
                knights?.GroupBuffsFor(side.Key, troopRequests.Select(t => t.Group!)) ?? new Dictionary<string, GroupBuffs>(),
                troopRequests.Select(t => t.Group!), tables.Normalization, tables.Ck3),
        };
    }
}
