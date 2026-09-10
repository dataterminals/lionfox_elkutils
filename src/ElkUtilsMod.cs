using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace lionfox_elkutils
{
    public class ElkUtilsConfig
    {
        public bool enabled = true;
        public int maxSearchRadius = 1000;
        public bool allowCrossPlayer = true;
    }

    [ProtoContract]
    public class ElkUtilsHotkeyPacket
    {
        // "recall" or "goto" — server runs the matching subcommand for the sender.
        [ProtoMember(1)] public string action = "";
    }

    // Universal mod. Server owns the elk-finder and teleport logic. Client registers default-
    // unbound hotkeys that forward to the server via a single packet type. ConfigLib applies its
    // patches before AssetsLoaded fires, so the config asset always reflects current GUI values.
    public class ElkUtilsMod : ModSystem
    {
        const string ConfigAssetPath = "lionfoxelkutils:config/lionfoxelkutils.json";
        const string ConfigFileName  = "lionfoxelkutils.json";
        const string NetChannelName  = "lionfoxelkutils";
        const string HotkeyRecall    = "elkutils-recall";
        const string HotkeyGoto      = "elkutils-goto";

        ElkUtilsConfig config = new ElkUtilsConfig();

        ICoreServerAPI? sapi;
        ICoreClientAPI? capi;
        IClientNetworkChannel? cChannel;

        public override void Start(ICoreAPI api)
        {
            api.Network.RegisterChannel(NetChannelName)
                .RegisterMessageType<ElkUtilsHotkeyPacket>();
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            LoadConfig(api);
        }

        void LoadConfig(ICoreAPI api)
        {
            try
            {
                var asset = api.Assets.TryGet(new AssetLocation(ConfigAssetPath));
                if (asset != null)
                {
                    config = asset.ToObject<ElkUtilsConfig>();
                    return;
                }
            }
            catch (Exception e)
            {
                api.Logger.Warning($"[ElkUtils] Asset config parse failed ({e.Message}); falling back to ModConfig.");
            }

            try
            {
                var loaded = api.LoadModConfig<ElkUtilsConfig>(ConfigFileName);
                if (loaded == null) api.StoreModConfig(config, ConfigFileName);
                else config = loaded;
            }
            catch (Exception e)
            {
                api.Logger.Warning($"[ElkUtils] ModConfig read failed ({e.Message}); using defaults.");
            }
        }

        // ============================================================
        //  CLIENT
        // ============================================================

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            cChannel = api.Network.GetChannel(NetChannelName);

            api.Input.RegisterHotKey(
                HotkeyRecall,
                Lang.Get("lionfoxelkutils:hotkey-recall"),
                GlKeys.Unknown,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(HotkeyRecall, _ => SendHotkey("recall"));

            api.Input.RegisterHotKey(
                HotkeyGoto,
                Lang.Get("lionfoxelkutils:hotkey-goto"),
                GlKeys.Unknown,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(HotkeyGoto, _ => SendHotkey("goto"));
        }

        bool SendHotkey(string action)
        {
            cChannel?.SendPacket(new ElkUtilsHotkeyPacket { action = action });
            return true;
        }

        // ============================================================
        //  SERVER
        // ============================================================

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            api.Network.GetChannel(NetChannelName)
                .SetMessageHandler<ElkUtilsHotkeyPacket>(OnHotkeyFromClient);

            var parsers = api.ChatCommands.Parsers;

            api.ChatCommands.Create("elk")
                .WithAlias("elkutils")
                .WithDescription(Lang.Get("lionfoxelkutils:cmd-desc"))
                .RequiresPrivilege(Privilege.controlserver)
                .RequiresPlayer()
                .BeginSubCommand("recall")
                    .WithDescription(Lang.Get("lionfoxelkutils:cmd-recall-desc"))
                    .HandleWith(OnCmdRecall)
                .EndSubCommand()
                .BeginSubCommand("goto")
                    .WithDescription(Lang.Get("lionfoxelkutils:cmd-goto-desc"))
                    .HandleWith(OnCmdGoto)
                .EndSubCommand()
                .BeginSubCommand("send")
                    .WithDescription(Lang.Get("lionfoxelkutils:cmd-send-desc"))
                    .WithArgs(parsers.OnlinePlayer("player"))
                    .HandleWith(OnCmdSend)
                .EndSubCommand()
                .BeginSubCommand("fetch")
                    .WithDescription(Lang.Get("lionfoxelkutils:cmd-fetch-desc"))
                    .WithArgs(parsers.OnlinePlayer("player"))
                    .HandleWith(OnCmdFetch)
                .EndSubCommand()
                .BeginSubCommand("move")
                    .WithDescription(Lang.Get("lionfoxelkutils:cmd-move-desc"))
                    .WithArgs(parsers.Double("x"), parsers.Double("y"), parsers.Double("z"))
                    .HandleWith(OnCmdMove)
                .EndSubCommand();
        }

        void OnHotkeyFromClient(IServerPlayer fromPlayer, ElkUtilsHotkeyPacket pkt)
        {
            // The hotkey path bypasses ChatCommand privilege checks, so re-check here.
            if (!fromPlayer.HasPrivilege(Privilege.controlserver))
            {
                Notify(fromPlayer, Lang.Get("lionfoxelkutils:msg-no-privilege"));
                return;
            }

            if (!config.enabled)
            {
                Notify(fromPlayer, Lang.Get("lionfoxelkutils:msg-disabled"));
                return;
            }

            switch (pkt.action)
            {
                case "recall": DoRecall(fromPlayer); break;
                case "goto":   DoGoto(fromPlayer); break;
            }
        }

        // ---------- subcommand handlers ----------

        TextCommandResult OnCmdRecall(TextCommandCallingArgs args)
        {
            var p = (IServerPlayer)args.Caller.Player;
            if (!config.enabled) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-disabled"));
            return DoRecall(p);
        }

        TextCommandResult OnCmdGoto(TextCommandCallingArgs args)
        {
            var p = (IServerPlayer)args.Caller.Player;
            if (!config.enabled) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-disabled"));
            return DoGoto(p);
        }

        TextCommandResult OnCmdSend(TextCommandCallingArgs args)
        {
            var p = (IServerPlayer)args.Caller.Player;
            if (!config.enabled) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-disabled"));
            if (!CrossPlayerAllowed(p)) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-cross-disabled"));

            var target = ResolveTarget(args, 0);
            if (target == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-target"));
            if (target.Entity == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-target-not-loaded", target.PlayerName));

            var elk = FindClosestOwnedElk(p);
            if (elk == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-elk"));

            var pos = target.Entity.Pos;
            TeleportEntity(elk, pos.X, pos.Y, pos.Z);
            LogAction(p, $"sent elk {elk.EntityId} to {target.PlayerName} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}).");
            return TextCommandResult.Success(Lang.Get("lionfoxelkutils:msg-send-ok", target.PlayerName));
        }

        TextCommandResult OnCmdFetch(TextCommandCallingArgs args)
        {
            var p = (IServerPlayer)args.Caller.Player;
            if (!config.enabled) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-disabled"));
            if (!CrossPlayerAllowed(p)) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-cross-disabled"));

            var target = ResolveTarget(args, 0);
            if (target == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-target"));
            if (target.Entity == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-target-not-loaded", target.PlayerName));

            var elk = FindClosestOwnedElk(p);
            if (elk == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-elk"));

            var pos = elk.Pos;
            TeleportEntity(target.Entity, pos.X, pos.Y, pos.Z);
            Notify(target, Lang.Get("lionfoxelkutils:msg-fetched-by", p.PlayerName));
            LogAction(p, $"fetched {target.PlayerName} to elk {elk.EntityId} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}).");
            return TextCommandResult.Success(Lang.Get("lionfoxelkutils:msg-fetch-ok", target.PlayerName));
        }

        TextCommandResult OnCmdMove(TextCommandCallingArgs args)
        {
            var p = (IServerPlayer)args.Caller.Player;
            if (!config.enabled) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-disabled"));

            double x = (double)args[0];
            double y = (double)args[1];
            double z = (double)args[2];

            var elk = FindClosestOwnedElk(p);
            if (elk == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-elk"));

            TeleportEntity(elk, x, y, z);
            LogAction(p, $"moved elk {elk.EntityId} to ({x:F1}, {y:F1}, {z:F1}).");
            return TextCommandResult.Success(Lang.Get("lionfoxelkutils:msg-move-ok", $"({x:F1}, {y:F1}, {z:F1})"));
        }

        // ---------- core actions (shared by commands + hotkeys) ----------

        TextCommandResult DoRecall(IServerPlayer p)
        {
            if (p.Entity == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-self-pos"));

            var elk = FindClosestOwnedElk(p);
            if (elk == null)
            {
                Notify(p, Lang.Get("lionfoxelkutils:msg-no-elk"));
                return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-elk"));
            }

            var pos = p.Entity.Pos;
            TeleportEntity(elk, pos.X, pos.Y, pos.Z);
            LogAction(p, $"recalled elk {elk.EntityId} to self at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}).");
            var msg = Lang.Get("lionfoxelkutils:msg-recall-ok");
            Notify(p, msg);
            return TextCommandResult.Success(msg);
        }

        TextCommandResult DoGoto(IServerPlayer p)
        {
            if (p.Entity == null) return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-self-pos"));

            var elk = FindClosestOwnedElk(p);
            if (elk == null)
            {
                Notify(p, Lang.Get("lionfoxelkutils:msg-no-elk"));
                return TextCommandResult.Error(Lang.Get("lionfoxelkutils:msg-no-elk"));
            }

            var pos = elk.Pos;
            TeleportEntity(p.Entity, pos.X, pos.Y, pos.Z);
            LogAction(p, $"teleported to elk {elk.EntityId} at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}).");
            var msg = Lang.Get("lionfoxelkutils:msg-goto-ok");
            Notify(p, msg);
            return TextCommandResult.Success(msg);
        }

        // ---------- helpers ----------

        // Finds the elk in loaded entities owned by `player` that is closest to them horizontally.
        // Returns null if no owned elk is in range or in a loaded chunk.
        Entity? FindClosestOwnedElk(IServerPlayer player)
        {
            if (sapi == null || player.Entity == null) return null;

            var ppos = player.Entity.Pos.XYZ;
            double maxR = config.maxSearchRadius;
            double maxRSq = maxR * maxR;

            Entity? best = null;
            double bestSq = double.MaxValue;

            // LoadedEntities is the authoritative dictionary of currently-loaded entities on the server.
            foreach (var kv in sapi.World.LoadedEntities)
            {
                var e = kv.Value;
                if (e == null || !e.Alive) continue;
                if (!IsElk(e)) continue;

                var bhv = e.GetBehavior<EntityBehaviorOwnable>();
                if (bhv == null) continue;
                if (!bhv.IsOwner(player.Entity)) continue;

                double dx = e.Pos.X - ppos.X;
                double dz = e.Pos.Z - ppos.Z;
                double dSq = dx * dx + dz * dz;
                if (dSq > maxRSq) continue;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    best = e;
                }
            }

            return best;
        }

        static bool IsElk(Entity e)
        {
            // Vanilla tamed elk entity codes are tameddeer-<type>-<gender>-<age> where <type>
            // is "elk" or "albinoelk". Matching the substring is tolerant of future variants
            // (e.g. baby tameddeer) while still excluding non-elk tameddeer variants if added.
            var path = e.Code?.Path;
            if (string.IsNullOrEmpty(path)) return false;
            if (!path.StartsWith("tameddeer-", StringComparison.OrdinalIgnoreCase)) return false;
            return path.Contains("elk", StringComparison.OrdinalIgnoreCase);
        }

        void TeleportEntity(Entity e, double x, double y, double z)
        {
            // TeleportToDouble handles loading the destination chunk on the server thread and
            // syncs the new position to clients. Mounted riders move with the mount.
            e.TeleportToDouble(x, y, z);
        }

        bool CrossPlayerAllowed(IServerPlayer p)
        {
            // Admins always pass — the toggle is for cases where multiple controlserver-level
            // admins coexist and the host wants to disable cross-player teleports across the board.
            if (config.allowCrossPlayer) return true;
            return p.HasPrivilege(Privilege.root);
        }

        static IServerPlayer? ResolveTarget(TextCommandCallingArgs args, int argIndex)
        {
            // The OnlinePlayer parser's value is an IPlayer (specifically IServerPlayer when
            // resolved on the server). Anything else means no match was registered.
            return args[argIndex] as IServerPlayer;
        }

        void Notify(IServerPlayer player, string msg)
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, msg, EnumChatType.Notification);
        }

        void LogAction(IServerPlayer p, string action)
        {
            sapi?.Logger.Notification($"[ElkUtils] {p.PlayerName} {action}");
        }
    }
}
