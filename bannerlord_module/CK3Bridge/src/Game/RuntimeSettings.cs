using System;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// In-memory overrides of the user's BannerlordConfig. Never calls BannerlordConfig.Save(); every override is
    /// undone before the engine's own Save at exit, which runs in Module.FinalizeModule (v1.4.8):
    ///   Game.OnFinalize -> GameStateManager.CleanStates -> Owner.OnStateStackEmpty
    ///   -> Game.Destroy -> MBGameManager.OnGameEnd -> our OnGameEnd -> RestoreAll
    ///   -> BannerlordConfig.Save() -> OnSubModuleUnloaded (too late for this).
    /// Options -> Apply also calls Save (ManagedOptions), so overrides are kept short: the focus suppression
    /// only spans the bridge's window swaps and BattleSize only the opener call.
    /// </summary>
    internal static class RuntimeSettings
    {
        private static bool? _userStopGameOnFocusLost;

        public static bool IsFocusPauseSuppressed => _userStopGameOnFocusLost != null;

        /// <summary>
        /// Losing focus in a mission opens the escape menu and pauses (MissionGauntletSingleplayerEscapeMenu
        /// .OnFocusChangeOnGameWindow) when StopGameOnFocusLost is on. Turn it off while the bridge moves focus.
        /// </summary>
        public static void SuppressFocusPause()
        {
            if (_userStopGameOnFocusLost == null) _userStopGameOnFocusLost = BannerlordConfig.StopGameOnFocusLost;
            BannerlordConfig.StopGameOnFocusLost = false;
        }

        public static void RestoreFocusPause()
        {
            if (_userStopGameOnFocusLost == null) return;
            BannerlordConfig.StopGameOnFocusLost = _userStopGameOnFocusLost.Value;
            _userStopGameOnFocusLost = null;
        }

        /// <summary>
        /// Runs <paramref name="openMission"/> with BannerlordConfig.BattleSize set to <paramref name="index"/>. The
        /// option is read only in the DefaultBattleMissionAgentSpawnLogic constructor,
        /// which runs synchronously inside MissionState.OpenNew, so the user's value is back
        /// before the opener returns.
        /// </summary>
        public static T WithBattleSize<T>(int index, Func<T> openMission)
        {
            int saved = BannerlordConfig.BattleSize;
            try
            {
                BannerlordConfig.BattleSize = index;
                return openMission();
            }
            finally
            {
                BannerlordConfig.BattleSize = saved;
            }
        }

        /// <summary>Undoes every override (managed config and the native frame limiter). Idempotent.</summary>
        public static void RestoreAll(bool applyNative)
        {
            RestoreFocusPause();
            WindowResidency.RestoreFrameLimiter(applyNative);
        }
    }
}
