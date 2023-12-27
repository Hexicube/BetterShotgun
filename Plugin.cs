using BepInEx;
using HarmonyLib;

namespace BetterShotgun
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Lethal Company.exe")]
    public class Plugin : BaseUnityPlugin
    {
        private Harmony h;
        private void Awake()
        {
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            ShotgunConfig.LoadConfig(Config);
            Logger.LogInfo("Config loaded");

            h = Harmony.CreateAndPatchAll(typeof(ShotgunPatch));
            h.PatchAll(typeof(ShotgunConfig));
        }

        private void OnDestroy()
        {
            h.UnpatchSelf();
        }
    }
}
