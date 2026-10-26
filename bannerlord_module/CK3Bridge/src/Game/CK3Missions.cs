using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.MissionSpawnHandlers;
using TaleWorlds.MountAndBlade.Missions.Handlers;
using TaleWorlds.MountAndBlade.Missions.MissionLogics;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.Source.Missions.Handlers.Logic;
using TaleWorlds.MountAndBlade.View;

namespace CK3Bridge
{
    /// <summary>Normalised opener inputs (see RequestRules / RequestExtras for the rules).</summary>
    internal sealed class MissionSetup
    {
        public string Scene;
        public string Season;
        public int PresetHour;
        public string SceneLevels;
        public int BattleSizeIndex;
        public int? BattleSizeOverride;                  // scale.battleSize; applied only above the largest preset (RequestRules)
        public RequestExtras Extras;
    }

    /// <summary>
    /// Opens the battle with our own field opener: the 26 behaviours of BannerlordMissions.OpenCustomBattleMission
    /// (Bannerlord v1.4.8) with four changes: mission name "CK3Battle"; both sides' general names
    /// for the captains logic (stock passes only the player side's); that logic is <see cref="CK3CaptainsAssignmentLogic"/>
    /// (heroes[].captainOf); and an atmosphere that may use a weather preset. Sieges stay in CK3 (PROTOCOL.md decision), so no
    /// siege mission is ever opened.
    /// CK3 logics (tracker, state decider, face guard) are attached in OnBeforeMissionBehaviorInitialize.
    /// </summary>
    internal static class CK3Missions
    {
        public const string OwnFieldMissionName = "CK3Battle";
        public const string StockFieldMissionName = "CustomBattle";

        /// <summary>
        /// THE switch: true opens field battles as "CK3Battle", whose views come from <see cref="CK3BattleViews"/>
        /// (stock views plus the CK3 presenter). If our view creator is somehow not registered, "CustomBattle"
        /// is used so the battle still gets the stock UI (a mission without views crashes in BattleObserverMissionLogic).
        /// </summary>
        private static readonly bool UseOwnFieldMissionName = true;

        public static string FieldMissionName =>
            UseOwnFieldMissionName && IsViewCreatorRegistered(OwnFieldMissionName) ? OwnFieldMissionName : StockFieldMissionName;

        public static Mission Open(MissionSetup setup, BattleArmies armies)
        {
            Game.Current.PlayerTroop = armies.PlayerCharacter;   // the agent built for this character gets the Player controller
            Mission mission = RuntimeSettings.WithBattleSize(setup.BattleSizeIndex, () => OpenField(setup, armies));
            if (setup.BattleSizeOverride.HasValue) ApplyBattleSizeOverride(mission, setup.BattleSizeOverride.Value);
            return mission;
        }

        /// <summary>
        /// Writes a field battle size above the largest preset over DefaultBattleMissionAgentSpawnLogic._battleSize (readonly,
        /// set in its constructor). Safe right after MissionState.OpenNew: the field is first read in InitWithSinglePhase,
        /// from CustomBattleMissionSpawnHandler.AfterStart, frames later (Bannerlord v1.4.8).
        /// </summary>
        private static void ApplyBattleSizeOverride(Mission mission, int requested)
        {
            var logic = mission?.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
            FieldInfo field = typeof(DefaultBattleMissionAgentSpawnLogic).GetField("_battleSize", BindingFlags.NonPublic | BindingFlags.Instance);
            if (logic == null || field == null || field.FieldType != typeof(int))
            {
                BridgeIo.Log($"field battle size override skipped: {(logic == null ? "no DefaultBattleMissionAgentSpawnLogic" : "DefaultBattleMissionAgentSpawnLogic._battleSize not found")}");
                return;
            }
            int cap = DefaultBattleMissionAgentSpawnLogic.MaxNumberOfAgentsForMission;
            int? value = RequestRules.FieldBattleSizeOverride(requested, cap);
            if (!value.HasValue) return;
            field.SetValue(logic, value.Value);
            BridgeIo.Log($"field battle size override: requested {requested}, applied {value.Value} troops (engine cap {cap} agents)");
            if (value.Value < requested)
                BridgeIo.Log($"warning: engine cap {cap} agents: no battle-size uncap mod active?");
        }

        private static Mission OpenField(MissionSetup setup, BattleArmies armies)
        {
            CustomBattleCombatant playerParty = armies.PlayerSide.Combatant;
            CustomBattleCombatant enemyParty = armies.EnemySide.Combatant;
            BattleSideEnum playerSide = playerParty.Side;
            bool isPlayerAttacker = armies.PlayerIsAttacker;
            bool isPlayerGeneral = armies.PlayerIsGeneral;
            bool isPlayerSergeant = !isPlayerGeneral;
            BasicCharacterObject playerCharacter = armies.PlayerCharacter;
            CustomBattleCombatant attackerParty = isPlayerAttacker ? playerParty : enemyParty;
            CustomBattleCombatant defenderParty = isPlayerAttacker ? enemyParty : playerParty;

            var troopSuppliers = new IMissionTroopSupplier[2];
            troopSuppliers[(int)playerParty.Side] = new CustomBattleTroopSupplier(playerParty, isPlayerSide: true, isPlayerGeneral, isSallyOut: false);
            troopSuppliers[(int)enemyParty.Side] = new CustomBattleTroopSupplier(enemyParty, isPlayerSide: false, isPlayerGeneral: false, isSallyOut: false);

            var record = new MissionInitializerRecord(setup.Scene)
            {
                DoNotUseLoadingScreen = false,
                PlayingInCampaignMode = false,
                AtmosphereOnCampaign = Atmosphere(setup),
                SceneLevels = setup.SceneLevels,
                DecalAtlasGroup = 2,
            };

            string missionName = FieldMissionName;
            BridgeIo.Log($"opening field mission '{missionName}' on {setup.Scene} ({setup.Season}, {setup.PresetHour}h, battle size index {setup.BattleSizeIndex})");
            Mission mission = MissionState.OpenNew(missionName, record, _ => new MissionBehavior[]
            {
                new DefaultBattleMissionAgentSpawnLogic(troopSuppliers, playerSide, Mission.BattleSizeType.Battle),
                new BattlePowerCalculationLogic(),
                new CustomBattleAgentLogic(),
                new BannerBearerLogic(),
                new CustomBattleMissionSpawnHandler(defenderParty, attackerParty),
                new MissionOptionsComponent(),
                new BattleEndLogic(),
                new BattleReinforcementsSpawnController(),
                new MissionCombatantsLogic(null, playerParty, defenderParty, attackerParty, Mission.MissionTeamAITypeEnum.FieldBattle, isPlayerSergeant),
                new BattleObserverMissionLogic(),
                new AgentHumanAILogic(),
                new AgentVictoryLogic(),
                new MissionAgentPanicHandler(),
                new BattleMissionAgentInteractionLogic(),
                new AgentMoraleInteractionLogic(),
                new AssignPlayerRoleInTeamMissionController(isPlayerGeneral, isPlayerSergeant, isPlayerInArmy: false,
                    isPlayerSergeant ? new List<string> { playerCharacter.StringId } : new List<string>()),
                new CK3CaptainsAssignmentLogic(armies.Attacker.General.GetName(), armies.Defender.General.GetName()),
                new EquipmentControllerLeaveLogic(),
                new MissionHardBorderPlacer(),
                new MissionBoundaryPlacer(),
                new MissionBoundaryCrossingHandler(),
                new HighlightsController(),
                new BattleHighlightsController(),
                new BattleDeploymentMissionController(isPlayerAttacker),
                new BattleDeploymentHandler(isPlayerAttacker),
                new MissionObjectiveLogic(),
            });
            mission.SetPlayerCanTakeControlOfAnotherAgentWhenDead();
            return mission;
        }

        /// <summary>
        /// Stock atmosphere (season id + preset by hour, BannerlordMissions.CreateAtmosphereInfoForMission). An
        /// optional weather preset from the request replaces the preset name when the file exists under
        /// Native/Atmospheres; rain, snow and fog densities are applied when given ([I], test BL-ATM).
        /// </summary>
        private static AtmosphereInfo Atmosphere(MissionSetup setup)
        {
            AtmosphereInfo info = BannerlordMissions.CreateAtmosphereInfoForMission(setup.Season, setup.PresetHour);
            RequestExtras extras = setup.Extras;
            if (extras.AtmospherePreset != null)
            {
                if (AtmospherePresetExists(extras.AtmospherePreset)) info.AtmosphereName = extras.AtmospherePreset;
                else BridgeIo.Log($"atmosphere preset '{extras.AtmospherePreset}' not found, keeping {info.AtmosphereName}");
            }
            if (extras.Rain.HasValue) info.RainInfo.Density = MBMath.ClampFloat(extras.Rain.Value, 0f, 1f);
            if (extras.Snow.HasValue) info.SnowInfo.Density = MBMath.ClampFloat(extras.Snow.Value, 0f, 1f);
            if (extras.FogDensity.HasValue) info.FogInfo.Density = MBMath.ClampFloat(extras.FogDensity.Value, 0f, 10f);
            return info;
        }

        private static bool AtmospherePresetExists(string name)
        {
            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return false;
            return File.Exists(System.IO.Path.Combine(ModuleHelper.GetModuleFullPath("Native"), "Atmospheres", name + ".xml"));
        }

        /// <summary>
        /// A scene the field opener can use: it exists (TaleWorlds.Engine Utilities.TryGetFullFilePathOfScene) and
        /// is not a siege or lords-hall map of the custom battle list (CustomGame.CustomBattleScenes), which need other
        /// openers. Village maps are fine; their forced scene level is returned so they load like custom battle's
        /// "Village" mode does (CustomBattleHelper.StartGame -> SceneLevel = forcedSceneLevel).
        /// </summary>
        public static bool IsFieldScene(string scene, out string forcedSceneLevel, out string reason)
        {
            forcedSceneLevel = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(scene) || !Utilities.TryGetFullFilePathOfScene(scene, out _))
            {
                reason = "not found";
                return false;
            }
            CustomGame game = CustomGame.Current;
            if (game == null) return true;
            foreach (CustomBattleSceneData data in game.CustomBattleScenes)
            {
                if (data.SceneID != scene) continue;
                if (data.IsSiegeMap || data.IsLordsHallMap)
                {
                    reason = data.IsSiegeMap ? "a siege map" : "a lords hall map";
                    return false;
                }
                forcedSceneLevel = string.IsNullOrEmpty(data.ForcedSceneLevel) ? null : data.ForcedSceneLevel;
                break;
            }
            return true;
        }

        /// <summary>
        /// Reads ViewCreatorManager's private registry (filled by CollectTypes from every
        /// assembly that references the View assembly). Defensive only: any reflection failure means "not registered".
        /// </summary>
        private static bool IsViewCreatorRegistered(string missionName)
        {
            try
            {
                FieldInfo field = typeof(ViewCreatorManager).GetField("_viewCreators", BindingFlags.Static | BindingFlags.NonPublic);
                return field?.GetValue(null) is IDictionary creators && creators.Contains(missionName);
            }
            catch (Exception e)
            {
                BridgeIo.Log("view creator lookup failed: " + e.Message);
                return false;
            }
        }
    }
}
