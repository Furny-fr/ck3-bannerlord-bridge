using System;
using System.Collections.Generic;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// Mission side of a CK3 battle: tags every human agent with its side, CK3 group and hero key (and gives a grouped
    /// agent its group's buffs, <see cref="CK3BuffRegistry"/>), counts what happens to it, decides killed vs wounded, and
    /// reports load / result / end to the <see cref="BattleSession"/>.
    ///
    /// Killed vs wounded: custom games register DefaultAgentDecideKilledOrUnconsciousModel, which returns death
    /// probability 1 (every casualty is Killed, verified in a live test). Mission.GetAgentState asks the first
    /// IAgentStateDecider behaviour before rolling, so this class decides with CK3 odds
    /// (RequestExtras: troops 0.3 = CK3 main-phase fatal share, times the side's troopDeathChanceMult; heroes per key).
    ///
    /// Hero kills: a blow is credited to the hero who dealt it. A horse charge is dealt by the mount, which is never tagged
    /// (only humans are), so it counts for the mount's rider, as TaleWorlds credits it (MissionScoreboardComponent,
    /// AgentMoraleInteractionLogic; PROTOCOL.md §13.4 item 4).
    ///
    /// Snapshot timing: OnEndMission runs from Mission.EndMissionInternal before agents are removed and cleared,
    /// so agents still standing then are survivors.
    ///
    /// Every override is guarded: these are engine callbacks and an exception must never reach the mission loop.
    /// </summary>
    internal sealed class BattleTracker : MissionLogic, IAgentStateDecider
    {
        private sealed class AgentRecord
        {
            public BattleSide Side;
            public string Group;
            public string HeroKey;
            public bool Removed;
        }

        private readonly BattleSession _session;
        private readonly BattleArmies _armies;
        private readonly BattleAccounting _accounting;
        private readonly Dictionary<Agent, AgentRecord> _records = new Dictionary<Agent, AgentRecord>();
        private readonly HashSet<string> _unrequestedLogged = new HashSet<string>();
        private MissionResult _missionResult;
        private bool _resultReady;
        private bool _retreated;
        private bool _finished;
        private float _fightStart = -1f;
        private float _resultTime = -1f;

        public BattleTracker(BattleSession session)
        {
            _session = session;
            _armies = session.Armies;
            _accounting = session.Armies.Accounting;
        }

        public override void AfterStart()
        {
            base.AfterStart();
            Guard(nameof(AfterStart), _session.OnMissionLoaded);
        }

        public override void OnDeploymentFinished()
        {
            base.OnDeploymentFinished();
            _fightStart = Mission.CurrentTime;
        }

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            base.OnAgentBuild(agent, banner);
            Guard(nameof(OnAgentBuild), () => Register(agent));
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, blow);
            Guard(nameof(OnAgentRemoved), () => Remove(affectedAgent, affectorAgent, agentState));
        }

        /// <summary>Consulted for downing blows only. Mounts (and any failure) fall back to the stock roll (AgentState.None).</summary>
        public AgentState GetAgentState(Agent affectedAgent, float deathProbability, out bool usedSurgery)
        {
            usedSurgery = false;
            try
            {
                if (!affectedAgent.IsHuman) return AgentState.None;
                RequestExtras extras = _session.Extras;
                float chance = _records.TryGetValue(affectedAgent, out AgentRecord record) && record.HeroKey != null
                    ? extras.DeathChanceForHero(record.HeroKey)
                    : BuffMath.TroopDeathChance(extras.TroopDeathChance, CK3BuffRegistry.Side(affectedAgent));
                return MBRandom.RandomFloat < chance ? AgentState.Killed : AgentState.Unconscious;
            }
            catch (Exception e)
            {
                BridgeIo.Log("GetAgentState failed: " + e.Message);
                return AgentState.None;
            }
        }

        public override void OnMissionResultReady(MissionResult missionResult)
        {
            base.OnMissionResultReady(missionResult);
            Guard(nameof(OnMissionResultReady), () =>
            {
                if (_finished) return;
                _missionResult = missionResult;
                _resultReady = true;
                _resultTime = Mission.CurrentTime;
                BridgeIo.Log($"mission result: {missionResult?.BattleState} playerVictory={missionResult?.PlayerVictory} enemyRetreated={missionResult?.EnemyRetreated}");
                _session.OnResultReady(Snapshot("result"));
            });
        }

        /// <summary>Tab -> Leave -> "retreat?" confirmed (BasicMissionHandler -> Mission.RetreatMission).</summary>
        public override void OnRetreatMission()
        {
            base.OnRetreatMission();
            _retreated = true;
        }

        public override void OnSurrenderMission()
        {
            base.OnSurrenderMission();
            _retreated = true;
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            Guard(nameof(OnEndMission), () =>
            {
                if (_finished) return;
                BattleResult final = Snapshot(_session.EndStatus(_resultReady || _retreated));
                _finished = true;
                _session.OnMissionEnded(final);
            });
        }

        private void Register(Agent agent)
        {
            if (_finished || !agent.IsHuman || agent.Character == null) return;
            if (!TrySide(agent, out BattleSide side, out BattleSideEnum engineSide)) return;

            var record = new AgentRecord { Side = side };
            if (_armies.HeroByCharacter.TryGetValue(agent.Character, out CreatedHero hero))
            {
                record.HeroKey = hero.Spec.Key;
            }
            else if (_armies.Ledger.TryTake((engineSide, agent.Character), out string group))
            {
                record.Group = group;
                GroupBuffs buffs = CK3BuffRegistry.TagAgentGroup(agent, engineSide, group);
                if (buffs != null) ApplyGroupBuffs(agent, buffs);
            }
            else if (_unrequestedLogged.Add(agent.Character.StringId))
            {
                BridgeIo.Log($"agent of '{agent.Character.StringId}' on {side} was not requested; counted in side totals only");
            }
            _records[agent] = record;
            _accounting.OnSpawned(side, record.Group, record.HeroKey);
        }

        /// <summary>
        /// The spawn-time part of a group's buffs (PROTOCOL.md §13.4 item 5): a larger or smaller health pool, starting full,
        /// and extra initial morale (Bannerlord clamped it to 15..100 at initialisation only). Damage factors act per hit.
        /// </summary>
        private static void ApplyGroupBuffs(Agent agent, GroupBuffs buffs)
        {
            if (buffs.HealthMult != 1f)
            {
                agent.BaseHealthLimit *= buffs.HealthMult;
                agent.HealthLimit *= buffs.HealthMult;
                agent.Health = agent.HealthLimit;
            }
            if (buffs.InitialMoraleAdd != 0f) agent.ChangeMorale(buffs.InitialMoraleAdd);
        }

        private void Remove(Agent affected, Agent affector, AgentState state)
        {
            if (_finished || !_records.TryGetValue(affected, out AgentRecord record) || record.Removed) return;
            AgentFate fate;
            switch (state)
            {
                case AgentState.Killed: fate = AgentFate.Killed; break;
                case AgentState.Unconscious: fate = AgentFate.Wounded; break;
                case AgentState.Routed: fate = AgentFate.Routed; break;
                default: return;   // Deleted/None/Active: not a battle outcome
            }
            record.Removed = true;
            Agent killerAgent = affector != null && affector.IsMount ? affector.RiderAgent : affector;
            string killer = killerAgent != null && _records.TryGetValue(killerAgent, out AgentRecord by) ? by.HeroKey : null;
            _accounting.OnRemoved(record.Side, record.Group, record.HeroKey, fate, killer);
        }

        private BattleResult Snapshot(string status)
        {
            bool playerIsAttacker = _armies.PlayerIsAttacker;
            string winner = "none";
            if (status != "cancelled" && status != "aborted")
            {
                if (_missionResult != null)
                    winner = BattleAccounting.WinnerFrom(_missionResult.BattleState.ToString(), _missionResult.PlayerVictory,
                        _missionResult.PlayerDefeated, playerIsAttacker);
                else if (_retreated)
                    winner = playerIsAttacker ? "defender" : "attacker";
            }
            // Mission time excludes pauses (the hold, the escape menu): deployment end -> result.
            float end = _resultTime >= 0f ? _resultTime : Mission.CurrentTime;
            float start = _fightStart >= 0f ? _fightStart : 0f;
            return _accounting.Snapshot(status, winner, _retreated && !_resultReady, end - start, null);
        }

        private static bool TrySide(Agent agent, out BattleSide side, out BattleSideEnum engineSide)
        {
            engineSide = agent.Team?.Side ?? agent.Origin?.BattleCombatant?.Side ?? BattleSideEnum.None;
            side = engineSide == BattleSideEnum.Attacker ? BattleSide.Attacker : BattleSide.Defender;
            return engineSide == BattleSideEnum.Attacker || engineSide == BattleSideEnum.Defender;
        }

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                BridgeIo.Log($"BattleTracker.{what} failed: {e}");
            }
        }
    }
}
