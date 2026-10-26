using System;
using CK3xBL.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>request.json -> validated BattleRequest + extras + normalised opener inputs.</summary>
    internal static class RequestIntake
    {
        public static BattleRequest Parse(string json, out RequestExtras extras)
        {
            JObject root;
            try
            {
                root = ProtocolJson.ParseObject(json);
            }
            catch (JsonException e)
            {
                throw new RequestException("request.json is not valid JSON: " + e.Message);
            }
            BattleRequest request;
            try
            {
                request = ProtocolJson.ToObject<BattleRequest>(root);
            }
            catch (JsonException e)
            {
                throw new RequestException("request.json does not match protocol 2: " + e.Message);
            }
            // A missing "protocol" keeps the DTO default (2): protocol 1 requests are a compatible subset.
            if (request.Protocol != ProtocolInfo.Version)
                throw new RequestException($"unsupported protocol {request.Protocol} (this module speaks {ProtocolInfo.Version})");
            if (string.IsNullOrWhiteSpace(request.Id)) throw new RequestException("request has no id");
            if (request.Presentation == null) request.Presentation = new Presentation();
            if (request.Scale == null) request.Scale = new ScaleInfo();
            extras = RequestExtras.From(root);
            // Only an explicit "preload": true holds the battle until "go" (the DTO default would hold every request
            // that omits it, e.g. protocol 1 requests without presentation).
            request.Presentation.Preload = extras.Preload;
            return request;
        }

        public static MissionSetup Plan(BattleRequest request, RequestExtras extras)
        {
            // Sieges stay in CK3 (PROTOCOL.md decision, 2026-10-03): every combat is a Bannerlord field battle.
            if (!string.Equals(request.Kind ?? "field", "field", StringComparison.OrdinalIgnoreCase))
                BridgeIo.Log($"kind '{request.Kind}' is fought as a field battle (sieges stay in CK3)");

            var setup = new MissionSetup
            {
                Scene = request.Scene,
                Season = RequestRules.Season(request.Season),
                PresetHour = RequestRules.PresetHour(request.TimeOfDay),
                SceneLevels = extras.SceneLevels ?? "",
                Extras = extras,
                // Without scale.battleSize the user's own battle size option stays in effect.
                BattleSizeIndex = extras.BattleSize.HasValue ? RequestRules.BattleSizeIndex(extras.BattleSize.Value) : BannerlordConfig.BattleSize,
                // Above the largest preset the exact troop count is written after the mission opens (RequestRules filters).
                BattleSizeOverride = extras.BattleSize,
            };
            if (!CK3Missions.IsFieldScene(setup.Scene, out string forcedLevel, out string reason))
            {
                BridgeIo.Log($"scene '{setup.Scene}' is {reason}; using {RequestRules.DefaultFieldScene}");
                setup.Scene = RequestRules.DefaultFieldScene;
                setup.SceneLevels = "";
            }
            else if (setup.SceneLevels.Length == 0 && forcedLevel != null)
            {
                setup.SceneLevels = forcedLevel;
            }
            return setup;
        }
    }
}
