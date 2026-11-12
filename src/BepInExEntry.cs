using BepInEx;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Lets the same DLL run from BepInEx\plugins when Anchor Chain is unavailable
    /// (e.g. not yet updated for a game release). Plugin.Boot makes sure only one copy
    /// boots if Anchor Chain finds another in the mod folder.
    ///
    /// Only an entry point: Plugin keeps its own GameObject, logger and ConfigFile,
    /// so nothing here depends on this component staying alive.
    /// </summary>
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class BepInExEntry : BaseUnityPlugin
    {
        private void Awake() => Plugin.Boot(fromAnchorChain: false);
    }
}
