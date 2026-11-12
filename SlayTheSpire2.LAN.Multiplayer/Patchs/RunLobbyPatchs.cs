using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Platform;
using SlayTheSpire2.LAN.Multiplayer.Models;
using SlayTheSpire2.LAN.Multiplayer.Services;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global

namespace SlayTheSpire2.LAN.Multiplayer.Patchs
{
    [HarmonyPatch]
    internal class RunLobbyConstructorPatchs
    {
        //Every lobby constructor that takes the net service, whatever its other parameters: the game reshapes these
        //between versions (v0.113 added isCustom to StartRunLobby), and an exact signature here broke the whole mod.
        //Some overloads chain to another (": this(...)"), so the prefix can run twice for one lobby; it's idempotent.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] lobbyTypes = [typeof(StartRunLobby), typeof(RunLobby), typeof(LoadRunLobby)];

            foreach (var lobbyType in lobbyTypes)
            {
                var constructors = lobbyType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                    .Where(constructor => constructor.GetParameters().Any(parameter =>
                        parameter.Name == "netService" && parameter.ParameterType == typeof(INetGameService)))
                    .ToList();

                if (constructors.Count == 0)
                    throw new MissingMethodException(lobbyType.FullName, ".ctor(..., INetGameService netService, ...)");

                foreach (var constructor in constructors)
                {
                    yield return constructor;
                }
            }
        }

        private static void Prefix(INetGameService netService)
        {
            if (netService.Platform == PlatformType.None)
            {
                var lanPlayerNameService = LanPlayerNameService.Instance;

                lanPlayerNameService.NetService = netService;

                netService.UnregisterMessageHandler<LanPlayerNameResponseMessage>(lanPlayerNameService
                    .HandleLanPlayerNameResponseMessage);
                netService.RegisterMessageHandler<LanPlayerNameResponseMessage>(lanPlayerNameService
                    .HandleLanPlayerNameResponseMessage);

                if (netService.Type == NetGameType.Host)
                {
                    netService.UnregisterMessageHandler<LanPlayerNameRequestMessage>(lanPlayerNameService
                        .HandleLanPlayerNameRequestMessage);
                    netService.RegisterMessageHandler<LanPlayerNameRequestMessage>(lanPlayerNameService
                        .HandleLanPlayerNameRequestMessage);
                }
            }
        }
    }

    [HarmonyPatch]
    internal class RunLobbyCleanUpPatchs
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

            yield return typeof(StartRunLobby).GetMethod("CleanUp", flags)!;
            yield return typeof(LoadRunLobby).GetMethod("CleanUp", flags)!;
        }

        private static void Prefix(object __instance, bool disconnectSession)
        {
            var netService = Traverse.Create(__instance).Property("NetService").GetValue<INetGameService>();

            if (netService.Platform == PlatformType.None)
            {
                var lanPlayerNameService = LanPlayerNameService.Instance;

                if (disconnectSession)
                {
                    lanPlayerNameService.SetDefaultPlayerNames();
                }

                netService.UnregisterMessageHandler<LanPlayerNameResponseMessage>(lanPlayerNameService
                    .HandleLanPlayerNameResponseMessage);

                if (netService.Type == NetGameType.Host)
                {
                    netService.UnregisterMessageHandler<LanPlayerNameRequestMessage>(lanPlayerNameService
                        .HandleLanPlayerNameRequestMessage);
                }
            }
        }

        [HarmonyPatch(typeof(RunLobby), "Dispose")]
        internal class RunLobbyDisposePatch
        {
            private static void Prefix(INetGameService ____netService)
            {
                if (____netService.Platform == PlatformType.None)
                {
                    var lanPlayerNameService = LanPlayerNameService.Instance;

                    lanPlayerNameService.SetDefaultPlayerNames();

                    ____netService.UnregisterMessageHandler<LanPlayerNameResponseMessage>(lanPlayerNameService
                        .HandleLanPlayerNameResponseMessage);

                    if (____netService.Type == NetGameType.Host)
                    {
                        ____netService.UnregisterMessageHandler<LanPlayerNameRequestMessage>(lanPlayerNameService
                            .HandleLanPlayerNameRequestMessage);
                    }
                }
            }
        }
    }
}