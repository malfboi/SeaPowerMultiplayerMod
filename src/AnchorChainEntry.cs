using AnchorChain;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Anchor Chain entry point. The mod ships as a Steam Workshop item loaded by
    /// Anchor Chain rather than as a DLL dropped into BepInEx/plugins.
    ///
    /// Anchor Chain enumerates this assembly's exported types to find us, which
    /// forces every referenced assembly to resolve before our code runs - see the
    /// ILRepackMerge target in the csproj for why LiteNetLib is merged in rather
    /// than shipped alongside.
    /// </summary>
    [ACPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class AnchorChainEntry : IAnchorChainMod
    {
        public void TriggerEntryPoint() => Plugin.Boot();
    }
}
