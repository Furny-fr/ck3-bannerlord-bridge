using System;
using CK3xBL.Protocol;

namespace CK3Bridge
{
    /// <summary>
    /// Pure normalisation rules for request.json values. Each rule cites the Bannerlord v1.4.8 code it protects against.
    /// </summary>
    public static class RequestRules
    {
        public const string DefaultFieldScene = "battle_terrain_029";

        /// <summary>
        /// Stock season ids are spring/summer/fall/winter (BannerlordMissions.CreateAtmosphereInfoForMission).
        /// A null season throws there and an unknown one silently becomes spring; "autumn" is CK3 wording.
        /// </summary>
        public static string Season(string season)
        {
            switch ((season ?? "").Trim().ToLowerInvariant())
            {
                case "spring": return "spring";
                case "summer": return "summer";
                case "fall":
                case "autumn": return "fall";
                case "winter": return "winter";
                default: return "summer";
            }
        }

        /// <summary>Hours that have a stock atmosphere preset (BannerlordMissions.CreateAtmosphereInfoForMission).</summary>
        public static readonly int[] PresetHours = { 6, 12, 15, 18, 22 };

        /// <summary>Snaps any hour to the nearest stock preset hour; ties go to the earlier hour.</summary>
        public static int PresetHour(float hour)
        {
            if (float.IsNaN(hour) || float.IsInfinity(hour)) return 12;
            float h = hour % 24f;
            if (h < 0f) h += 24f;
            int best = PresetHours[0];
            float bestDistance = float.MaxValue;
            foreach (int preset in PresetHours)
            {
                float distance = Math.Abs(preset - h);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = preset;
                }
            }
            return best;
        }

        /// <summary>
        /// The CK3 group a troop request's men belong to: its group, or its troop id when the group is empty (PROTOCOL.md
        /// §6.1). The key of result groups, of the group ledger and of groupBuffs; null only when both are missing.
        /// </summary>
        public static string GroupKey(TroopRequest troop) => string.IsNullOrEmpty(troop.Group) ? troop.Id : troop.Group;

        public static bool IsPlayerAttacker(string playerSide) =>
            !string.Equals((playerSide ?? "").Trim(), "defender", StringComparison.OrdinalIgnoreCase);

        /// <summary>Field battle sizes per BannerlordConfig.BattleSize index (BannerlordConfig._battleSizes).</summary>
        public static readonly int[] FieldBattleSizes = { 200, 300, 400, 500, 600, 800, 1000 };

        /// <summary>
        /// Index of the BannerlordConfig.BattleSize option whose agent count is closest to <paramref name="agents"/>
        /// (ties go to the larger size). The value only matters while DefaultBattleMissionAgentSpawnLogic is constructed.
        /// </summary>
        public static int BattleSizeIndex(int agents)
        {
            int best = 0;
            for (int i = 1; i < FieldBattleSizes.Length; i++)
                if (Math.Abs(FieldBattleSizes[i] - agents) <= Math.Abs(FieldBattleSizes[best] - agents)) best = i;
            return best;
        }

        /// <summary>Clamps a probability to 0..1; NaN falls back to <paramref name="fallback"/>.</summary>
        public static float Probability(float value, float fallback)
        {
            if (float.IsNaN(value)) return fallback;
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        /// <summary>"Redmean" weighted RGB distance between two ARGB colours (0 = identical, ~765 = black vs white).</summary>
        public static double ColorDistance(uint a, uint b)
        {
            int ar = (int)((a >> 16) & 255), ag = (int)((a >> 8) & 255), ab = (int)(a & 255);
            int br = (int)((b >> 16) & 255), bg = (int)((b >> 8) & 255), bb = (int)(b & 255);
            double rMean = (ar + br) / 2.0;
            int dr = ar - br, dg = ag - bg, db = ab - bb;
            return Math.Sqrt((2 + rMean / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rMean) / 256) * db * db);
        }

        /// <summary>Team colours closer than this are swapped for the enemy (PROTOCOL.md §7.2).</summary>
        public const double ClashDistance = 60.0;
    }
}
