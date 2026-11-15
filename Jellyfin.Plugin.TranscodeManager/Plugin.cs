using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.TranscodeManager.Configuration;

namespace Jellyfin.Plugin.TranscodeManager;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "6f0d5a42-3b1c-4e8a-9a57-2c1d7b94e0a3";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Transcode Manager";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "Find large H.264 files, queue H.265 transcodes for a nightly window, and compare before replacing.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "TranscodeManager",
            DisplayName = "Transcode Manager",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "compress",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
