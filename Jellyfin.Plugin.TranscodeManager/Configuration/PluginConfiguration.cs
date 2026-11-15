using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TranscodeManager.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Start of the nightly window (server local time, HH:mm).</summary>
    public string WindowStart { get; set; } = "01:00";

    /// <summary>End of the nightly window (may be earlier than start = crosses midnight).</summary>
    public string WindowEnd { get; set; } = "07:00";

    /// <summary>hevc_nvenc (GPU) or libx265 (CPU).</summary>
    public string Encoder { get; set; } = "hevc_nvenc";

    /// <summary>NVENC: p1..p7. libx265: ultrafast..veryslow.</summary>
    public string Preset { get; set; } = "p7";

    /// <summary>NVENC -cq / x265 -crf. Lower = better quality, bigger file.</summary>
    public int Cq { get; set; } = 24;

    /// <summary>Empty = use Jellyfin's configured ffmpeg.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Empty = Jellyfin cache dir/transcodemanager. Must NOT be inside a library folder.</summary>
    public string StagingDir { get; set; } = string.Empty;
}
