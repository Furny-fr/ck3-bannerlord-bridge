using System;
using System.Collections.Generic;
using System.Linq;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// The CK3 buffs of the battle being fought (PROTOCOL.md §13.4): side buffs by team side, hero
    /// entries by hero character, group buffs by (side, group key) and, once the ledger has assigned an agent, by agent. Filled
    /// by <see cref="BattleSession.Start"/> after the armies are built and before the mission opens; emptied when the session
    /// ends, fails to open, or the game ends. Main thread only (the models below run on the main thread).
    ///
    /// Spawn order (Mission.SpawnAgent, v1.4.8): stats and initial morale are computed inside
    /// Agent.Build, before any OnAgentBuild, so the models look heroes up by character and sides by team only; group buffs
    /// are read at hit time, or applied by <see cref="BattleTracker"/> when it tags the agent.
    /// Every value is clamped once here (<see cref="BuffMath"/>), so nothing a request carries can throw or explode later.
    /// </summary>
    internal static class CK3BuffRegistry
    {
        /// <summary>What a hero carries besides his side's and group's buffs.</summary>
        internal sealed class HeroEntry
        {
            public string Key;
            public float DamageMultiplier = 1f;
            public HeroBuffs Buffs;

            /// <summary>The parsed heroes[].captainOf; null = stock captain assignment.</summary>
            public FormationClass? CaptainOf;
        }

        private static readonly Dictionary<BattleSideEnum, SideBuffs> Sides = new Dictionary<BattleSideEnum, SideBuffs>();
        private static readonly Dictionary<BasicCharacterObject, HeroEntry> Heroes = new Dictionary<BasicCharacterObject, HeroEntry>();
        private static readonly Dictionary<(BattleSideEnum, string), GroupBuffs> GroupsByKey = new Dictionary<(BattleSideEnum, string), GroupBuffs>();
        private static readonly Dictionary<Agent, GroupBuffs> GroupsByAgent = new Dictionary<Agent, GroupBuffs>();
        private static readonly HashSet<string> ReportedFailures = new HashSet<string>(StringComparer.Ordinal);

        public static bool Active { get; private set; }

        public static void Begin(BattleRequest request, BattleArmies armies)
        {
            End();
            int heroesBuffed = 0;
            var captains = new List<string>();
            foreach (BuiltSide side in new[] { armies.Attacker, armies.Defender })
            {
                string label = side.Side == BattleSide.Attacker ? "attacker" : "defender";
                SideBuffs sideBuffs = BuffMath.Sanitize(side.Request.Buffs);
                if (sideBuffs != null) Sides[side.EngineSide] = sideBuffs;
                AddGroups(side, label);
                foreach (CreatedHero hero in side.Heroes)
                {
                    HeroEntry entry = HeroEntryFor(hero.Spec, label);
                    Heroes[hero.Character] = entry;
                    if (entry.DamageMultiplier != 1f || entry.Buffs != null) heroesBuffed++;
                    if (entry.CaptainOf.HasValue) captains.Add($"{entry.Key} -> {entry.CaptainOf.Value}");
                }
            }
            Active = true;
            string sides = Sides.Count == 0 ? "none" : string.Join(" + ", Sides.Keys.Select(s => s == BattleSideEnum.Attacker ? "attacker" : "defender"));
            BridgeIo.Log($"{request.Id}: combat buffs: side buffs {sides}, {GroupsByKey.Count} group(s), {heroesBuffed} hero(es) buffed, " +
                         "captains: " + (captains.Count == 0 ? "stock" : string.Join(", ", captains)));
        }

        public static void End()
        {
            Active = false;
            Sides.Clear();
            Heroes.Clear();
            GroupsByKey.Clear();
            GroupsByAgent.Clear();
            ReportedFailures.Clear();
        }

        /// <summary>
        /// The ledger gave <paramref name="agent"/> to <paramref name="group"/> (BattleTracker, OnAgentBuild): hits see the
        /// group's buffs from now on. Returns them so the caller can apply the spawn-time part (health, morale); null = none.
        /// </summary>
        public static GroupBuffs TagAgentGroup(Agent agent, BattleSideEnum side, string group)
        {
            if (!Active || agent == null || group == null || !GroupsByKey.TryGetValue((side, group), out GroupBuffs buffs)) return null;
            GroupsByAgent[agent] = buffs;
            return buffs;
        }

        public static SideBuffs Side(Agent agent)
        {
            if (!Active || agent?.Team == null) return null;
            return Sides.TryGetValue(agent.Team.Side, out SideBuffs buffs) ? buffs : null;
        }

        /// <summary>The hero entry of a human agent built for a CK3 hero; null for everyone else (mounts included).</summary>
        public static HeroEntry Hero(Agent agent)
        {
            if (!Active || agent == null || !agent.IsHuman || agent.Character == null) return null;
            return Heroes.TryGetValue(agent.Character, out HeroEntry entry) ? entry : null;
        }

        public static FormationClass? CaptainOf(Agent agent) => Hero(agent)?.CaptainOf;

        /// <summary>Everything <paramref name="agent"/> carries; an unbuffed (default) value for null or outside a CK3 battle.</summary>
        public static AgentBuffs For(Agent agent)
        {
            if (!Active || agent == null) return default;
            HeroEntry hero = Hero(agent);
            GroupsByAgent.TryGetValue(agent, out GroupBuffs group);
            return new AgentBuffs(Side(agent), hero?.DamageMultiplier ?? 1f, hero?.Buffs, group);
        }

        /// <summary>A model or logic failed inside an engine callback: logged once per place and battle, the stock value is used.</summary>
        public static void Report(string where, Exception e)
        {
            if (ReportedFailures.Add(where)) BridgeIo.Log($"combat buffs: {where} failed, stock value used (logged once): {e}");
        }

        /// <summary>groupBuffs of one side; a key its troops do not carry is logged and ignored (PROTOCOL.md §13.4 item 3).</summary>
        private static void AddGroups(BuiltSide side, string label)
        {
            Dictionary<string, GroupBuffs> groups = BuffMath.GroupBuffsOf(side.Request,
                key => BridgeIo.Log($"{label}: groupBuffs key '{key}' names no requested troop group; ignored"));
            foreach (KeyValuePair<string, GroupBuffs> kv in groups) GroupsByKey[(side.EngineSide, kv.Key)] = kv.Value;
        }

        private static HeroEntry HeroEntryFor(HeroSpec spec, string label)
        {
            var entry = new HeroEntry
            {
                Key = spec.Key,
                DamageMultiplier = BuffMath.HeroDamageMultiplier(spec.DamageMultiplier),
                Buffs = BuffMath.Sanitize(spec.Buffs),
            };
            // Parsed once here, so the captain logic never handles request text inside an engine callback.
            if (BuffMath.TryParseCaptainOf(spec.CaptainOf, out CaptainFormation formation)) entry.CaptainOf = (FormationClass)(int)formation;
            else if (spec.CaptainOf != null) BridgeIo.Log($"{label}: hero {spec.Key}: captainOf '{spec.CaptainOf}' is not Infantry, Ranged, Cavalry or HorseArcher; ignored");
            return entry;
        }
    }

    /// <summary>
    /// Every CK3 damage factor in one place (PROTOCOL.md §13.4 item 4): sides, heroes and groups, on post-armour damage of
    /// every hit (AgentApplyDamageModel.CalculateDamage ends with ApplyGeneralDamageModifiers, which the stock model leaves
    /// alone). Falls, friendly fire and hits on mounts keep the stock damage; a horse charge is credited to the rider.
    /// </summary>
    public sealed class CK3AgentApplyDamageModel : CustomAgentApplyDamageModel
    {
        public override float ApplyGeneralDamageModifiers(in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage)
        {
            float damage = base.ApplyGeneralDamageModifiers(in attackInformation, in collisionData, baseDamage);
            if (!CK3BuffRegistry.Active || collisionData.IsFallDamage || attackInformation.IsFriendlyFire || attackInformation.IsVictimAgentMount)
                return damage;
            try
            {
                Agent attacker = attackInformation.IsAttackerAgentMount ? attackInformation.AttackerAgent?.RiderAgent : attackInformation.AttackerAgent;
                Agent victim = attackInformation.VictimAgent;
                bool missile = collisionData.IsMissile;
                bool charge = collisionData.IsHorseCharge || (!missile && attacker != null && attacker.HasMount);
                return damage * BuffMath.HitMultiplier(CK3BuffRegistry.For(attacker), CK3BuffRegistry.For(victim), missile, charge,
                    victimIsHero: victim != null && victim.IsHero);
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(ApplyGeneralDamageModifiers), e);
                return damage;
            }
        }
    }

    /// <summary>
    /// Hero buffs that are agent stats: speed, swing speed, ranged accuracy, knock-down/back and dismount resistance.
    /// UpdateAgentStats recomputes from scratch every time it runs, so the factors are applied after base and never compound.
    /// Skill bonuses travel in heroes[].skills, so GetEffectiveSkill stays stock.
    /// </summary>
    public sealed class CK3AgentStatCalculateModel : CustomBattleAgentStatCalculateModel
    {
        public override void UpdateAgentStats(Agent agent, AgentDrivenProperties agentDrivenProperties)
        {
            base.UpdateAgentStats(agent, agentDrivenProperties);
            try
            {
                HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
                if (hero == null) return;
                agentDrivenProperties.MaxSpeedMultiplier *= hero.SpeedMult;
                agentDrivenProperties.CombatMaxSpeedMultiplier *= hero.SpeedMult;
                agentDrivenProperties.SwingSpeedMultiplier *= hero.SwingSpeedMult;
                agentDrivenProperties.ThrustOrRangedReadySpeedMultiplier *= hero.SwingSpeedMult;
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(UpdateAgentStats), e);
            }
        }

        public override float GetWeaponInaccuracy(Agent agent, WeaponComponentData weapon, int weaponSkill)
        {
            float value = base.GetWeaponInaccuracy(agent, weapon, weaponSkill);
            HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
            return hero != null && weapon != null && weapon.IsRangedWeapon ? value * hero.RangedInaccuracyMult : value;
        }

        public override float GetKnockDownResistance(Agent agent, StrikeType strikeType = StrikeType.Invalid)
        {
            float value = base.GetKnockDownResistance(agent, strikeType);
            HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
            return hero == null ? value : BuffMath.Resistance(value, hero.KnockdownResistanceAdd);
        }

        public override float GetKnockBackResistance(Agent agent)
        {
            float value = base.GetKnockBackResistance(agent);
            HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
            return hero == null ? value : BuffMath.Resistance(value, hero.KnockdownResistanceAdd);
        }

        /// <summary>Half the knock-down bonus: staying in the saddle is the rider's skill more than his grit.</summary>
        public override float GetDismountResistance(Agent agent)
        {
            float value = base.GetDismountResistance(agent);
            HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
            return hero == null ? value : BuffMath.Resistance(value, 0.5f * hero.KnockdownResistanceAdd);
        }
    }

    /// <summary>
    /// Initial morale + side and hero initialMoraleAdd (CommonAIComponent.InitializeMorale then clamps to 15..100); heroes with
    /// noPanic never flee for low morale (CommonAIComponent.CanPanic asks CanPanicDueToMorale first).
    /// </summary>
    public sealed class CK3BattleMoraleModel : CustomBattleMoraleModel
    {
        public override float GetEffectiveInitialMorale(Agent agent, float baseMorale)
        {
            float morale = base.GetEffectiveInitialMorale(agent, baseMorale);
            return CK3BuffRegistry.Active ? BuffMath.InitialMorale(morale, CK3BuffRegistry.For(agent)) : morale;
        }

        public override bool CanPanicDueToMorale(Agent agent)
        {
            return base.CanPanicDueToMorale(agent) && !(CK3BuffRegistry.Hero(agent)?.Buffs?.NoPanic ?? false);
        }
    }
}
