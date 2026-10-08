using System;
using System.Collections.Generic;
using CK3xBL.Protocol;

namespace CK3Bridge
{
    /// <summary>
    /// The formations a hero may be asked to captain (heroes[].captainOf, PROTOCOL.md §13.4 item 2). The values equal
    /// TaleWorlds.Core.FormationClass (Infantry 0, Ranged 1, Cavalry 2, HorseArcher 3; pinned by the self-check), so the game
    /// code casts; kept here so the headless self-check can test the parse without game assemblies.
    /// </summary>
    public enum CaptainFormation
    {
        Infantry = 0,
        Ranged = 1,
        Cavalry = 2,
        HorseArcher = 3,
    }

    /// <summary>
    /// What one agent carries into a hit or a spawn: its side's buffs, and its hero's or group's when it has them. A null
    /// member is neutral. The default value is an unbuffed agent.
    /// </summary>
    public readonly struct AgentBuffs
    {
        public AgentBuffs(SideBuffs side, float heroDamageMultiplier, HeroBuffs hero, GroupBuffs group)
        {
            Side = side;
            HeroDamageMultiplier = heroDamageMultiplier;
            Hero = hero;
            Group = group;
        }

        public SideBuffs Side { get; }

        /// <summary>heroes[].damageMultiplier of a hero agent; 0 (the default value) and 1 both mean "no hero factor".</summary>
        public float HeroDamageMultiplier { get; }

        public HeroBuffs Hero { get; }
        public GroupBuffs Group { get; }
    }

    /// <summary>
    /// Pure arithmetic of the CK3 combat buffs (PROTOCOL.md §13.4): the module's safety clamps, the
    /// per-hit damage product and the spawn-time values. Game types stay out (src/Game/CK3Buffs.cs applies the results), so the
    /// headless self-check tests every rule here.
    /// </summary>
    public static class BuffMath
    {
        /// <summary>Clamp of every *Mult the request carries (PROTOCOL.md §13.4 item 7; the bridge clamps tighter).</summary>
        public const float MinMult = 0.25f;
        public const float MaxMult = 4f;
        public const float MaxMoraleAdd = 50f;
        public const float MinStartHealthFraction = 0.05f;
        public const float MaxKnockdownResistanceAdd = 0.5f;
        public const float MinHeroDamageMultiplier = 0.25f;
        public const float MaxHeroDamageMultiplier = 3f;

        /// <summary>Clamp of the whole per-hit product (PROTOCOL.md §13.4 item 4).</summary>
        public const float MinHitMultiplier = 0.25f;
        public const float MaxHitMultiplier = 3f;

        /// <summary>Clamped to [lo, hi]; NaN and infinities (which Newtonsoft reads from "NaN"/"Infinity" or overflowing numbers) become <paramref name="neutral"/>.</summary>
        public static float Safe(float value, float lo, float hi, float neutral)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return neutral;
            return value < lo ? lo : value > hi ? hi : value;
        }

        public static float Mult(float value) => Safe(value, MinMult, MaxMult, 1f);
        public static float MoraleAdd(float value) => Safe(value, -MaxMoraleAdd, MaxMoraleAdd, 0f);
        public static float HeroDamageMultiplier(float value) => Safe(value, MinHeroDamageMultiplier, MaxHeroDamageMultiplier, 1f);

        /// <summary>A clamped copy (the request object is left as it was read); null stays null.</summary>
        public static SideBuffs Sanitize(SideBuffs buffs)
        {
            if (buffs == null) return null;
            return new SideBuffs
            {
                DamageDealtMult = Mult(buffs.DamageDealtMult),
                DamageTakenMult = Mult(buffs.DamageTakenMult),
                InitialMoraleAdd = MoraleAdd(buffs.InitialMoraleAdd),
                ChargeDamageMult = Mult(buffs.ChargeDamageMult),
                TroopDeathChanceMult = Mult(buffs.TroopDeathChanceMult),
            };
        }

        public static GroupBuffs Sanitize(GroupBuffs buffs)
        {
            if (buffs == null) return null;
            return new GroupBuffs
            {
                DamageDealtMult = Mult(buffs.DamageDealtMult),
                DamageTakenMult = Mult(buffs.DamageTakenMult),
                HealthMult = Mult(buffs.HealthMult),
                InitialMoraleAdd = MoraleAdd(buffs.InitialMoraleAdd),
            };
        }

        public static HeroBuffs Sanitize(HeroBuffs buffs)
        {
            if (buffs == null) return null;
            return new HeroBuffs
            {
                DamageDealtMeleeMult = Mult(buffs.DamageDealtMeleeMult),
                DamageDealtRangedMult = Mult(buffs.DamageDealtRangedMult),
                DamageDealtVsHeroMult = Mult(buffs.DamageDealtVsHeroMult),
                DamageTakenMult = Mult(buffs.DamageTakenMult),
                ChargeDamageMult = Mult(buffs.ChargeDamageMult),
                InitialMoraleAdd = MoraleAdd(buffs.InitialMoraleAdd),
                NoPanic = buffs.NoPanic,
                StartHealthFraction = Safe(buffs.StartHealthFraction, MinStartHealthFraction, 1f, 1f),
                SpeedMult = Mult(buffs.SpeedMult),
                SwingSpeedMult = Mult(buffs.SwingSpeedMult),
                RangedInaccuracyMult = Mult(buffs.RangedInaccuracyMult),
                KnockdownResistanceAdd = Safe(buffs.KnockdownResistanceAdd, -MaxKnockdownResistanceAdd, MaxKnockdownResistanceAdd, 0f),
            };
        }

        /// <summary>
        /// The groupBuffs of one side, clamped and keyed verbatim (PROTOCOL.md §13.4 item 3). A key that no troop of the side
        /// carries (<see cref="RequestRules.GroupKey"/>) is passed to <paramref name="unknownKey"/> and left out; a null
        /// dictionary or entry counts as empty.
        /// </summary>
        public static Dictionary<string, GroupBuffs> GroupBuffsOf(SideRequest side, Action<string> unknownKey)
        {
            var result = new Dictionary<string, GroupBuffs>(StringComparer.Ordinal);
            if (side?.GroupBuffs == null || side.GroupBuffs.Count == 0) return result;
            var requested = new HashSet<string>(StringComparer.Ordinal);
            if (side.Troops != null)
            {
                foreach (TroopRequest troop in side.Troops)
                {
                    string key = troop == null ? null : RequestRules.GroupKey(troop);
                    if (key != null) requested.Add(key);
                }
            }
            foreach (KeyValuePair<string, GroupBuffs> kv in side.GroupBuffs)
            {
                if (kv.Key == null || kv.Value == null) continue;
                if (requested.Contains(kv.Key)) result[kv.Key] = Sanitize(kv.Value);
                else unknownKey?.Invoke(kv.Key);
            }
            return result;
        }

        /// <summary>
        /// The factor on one hit (PROTOCOL.md §13.4 item 4): attacker side dealt (× charge on a charge), hero damageMultiplier,
        /// hero melee or ranged dealt (× vs-hero on a melee hit against a hero, × hero charge on a charge), group dealt; victim
        /// side, hero and group taken; the product clamped to 0.25..3. <paramref name="charge"/> = a horse charge or a melee hit
        /// by a mounted agent. The caller skips falls, friendly fire and hits on mounts.
        /// </summary>
        public static float HitMultiplier(in AgentBuffs attacker, in AgentBuffs victim, bool missile, bool charge, bool victimIsHero)
        {
            float mult = 1f;
            if (attacker.Side != null)
            {
                mult *= attacker.Side.DamageDealtMult;
                if (charge) mult *= attacker.Side.ChargeDamageMult;
            }
            if (attacker.HeroDamageMultiplier > 0f) mult *= attacker.HeroDamageMultiplier;
            if (attacker.Hero != null)
            {
                mult *= missile ? attacker.Hero.DamageDealtRangedMult : attacker.Hero.DamageDealtMeleeMult;
                if (!missile && victimIsHero) mult *= attacker.Hero.DamageDealtVsHeroMult;
                if (charge) mult *= attacker.Hero.ChargeDamageMult;
            }
            if (attacker.Group != null) mult *= attacker.Group.DamageDealtMult;
            if (victim.Side != null) mult *= victim.Side.DamageTakenMult;
            if (victim.Hero != null) mult *= victim.Hero.DamageTakenMult;
            if (victim.Group != null) mult *= victim.Group.DamageTakenMult;
            return Safe(mult, MinHitMultiplier, MaxHitMultiplier, 1f);
        }

        /// <summary>Initial morale before Bannerlord's own 15..100 clamp: + side and hero initialMoraleAdd.</summary>
        public static float InitialMorale(float morale, in AgentBuffs agent) =>
            morale + (agent.Side?.InitialMoraleAdd ?? 0f) + (agent.Hero?.InitialMoraleAdd ?? 0f);

        /// <summary>A downed troop's killed-vs-unconscious chance × the side's troopDeathChanceMult, clamped to 0..1.</summary>
        public static float TroopDeathChance(float chance, SideBuffs side) =>
            Safe(chance * (side?.TroopDeathChanceMult ?? 1f), 0f, 1f, chance);

        /// <summary>A knock-down, knock-back or dismount resistance plus the hero's knockdownResistanceAdd, kept a share (0..1).</summary>
        public static float Resistance(float value, float add) => Safe(value + add, 0f, 1f, value);

        /// <summary>
        /// A hero's starting health when his buffs say he starts wounded (startHealthFraction &lt; 1): that share of his final
        /// pool (after healthMultiplier), at least 1. False when he starts at full health.
        /// </summary>
        public static bool TryStartHealth(float healthLimit, HeroBuffs hero, out float health)
        {
            health = healthLimit;
            if (hero == null || !(hero.StartHealthFraction < 1f)) return false;
            health = Math.Max(1f, healthLimit * hero.StartHealthFraction);
            return true;
        }

        /// <summary>
        /// heroes[].captainOf: one of Infantry, Ranged, Cavalry, HorseArcher, case-insensitive (surrounding blanks ignored).
        /// Anything else (null, empty, numbers, General, Bodyguard, ...) is no captaincy.
        /// </summary>
        public static bool TryParseCaptainOf(string value, out CaptainFormation formation)
        {
            formation = CaptainFormation.Infantry;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string name = value.Trim();
            foreach (CaptainFormation candidate in (CaptainFormation[])Enum.GetValues(typeof(CaptainFormation)))
            {
                if (!string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                formation = candidate;
                return true;
            }
            return false;
        }
    }
}
