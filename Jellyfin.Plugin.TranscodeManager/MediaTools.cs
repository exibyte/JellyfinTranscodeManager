using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.TranscodeManager;

public record ProbeResult(double Duration, string VideoCodec, int AudioStreams, int SubtitleStreams);

/// <summary>Thin wrappers around ffmpeg/ffprobe (Jellyfin's own binaries by default).</summary>
public class MediaTools
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly IMediaEncoder _encoder;

    public MediaTools(IMediaEncoder encoder)
    {
        _encoder = encoder;
    }

    public string Ffmpeg
    {
        get
        {
            var p = Plugin.Instance?.Configuration.FfmpegPath;
            return string.IsNullOrWhiteSpace(p) ? _encoder.EncoderPath : p;
        }
    }

    public string Ffprobe => _encoder.ProbePath;

    public static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    public async Task<ProbeResult?> ProbeAsync(string path, CancellationToken ct)
    {
        using var p = Process.Start(Psi(Ffprobe, new[]
        {
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path
        }));
        if (p == null)
        {
            return null;
        }

        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var json = await outTask;
        await errTask;
        if (p.ExitCode != 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var fmt)
            && fmt.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, Inv, out var dv))
        {
            duration = dv;
        }

        string vcodec = string.Empty;
        int audio = 0, subs = 0;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && vcodec.Length == 0)
                {
                    vcodec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                }
                else if (type == "audio")
                {
                    audio++;
                }
                else if (type == "subtitle")
                {
                    subs++;
                }
            }
        }

        return new ProbeResult(duration, vcodec, audio, subs);
    }

    /// <summary>Lossless PNG of a single frame, for visual comparison.</summary>
    public async Task<byte[]?> FrameAsync(string path, double seconds, CancellationToken ct)
    {
        using var p = Process.Start(Psi(Ffmpeg, new[]
        {
            "-v", "error", "-nostdin",
            "-ss", seconds.ToString("F2", Inv),
            "-i", path,
            "-frames:v", "1",
            "-f", "image2pipe", "-c:v", "png", "pipe:1"
        }));
        if (p == null)
        {
            return null;
        }

        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        await errTask;
        return p.ExitCode == 0 && ms.Length > 0 ? ms.ToArray() : null;
    }

    /// <summary>SSIM of a sample window (default 2 min) - objective quality hint, 1.0 = identical.</summary>
    public async Task<double?> SsimAsync(string distorted, string reference, double start, double seconds, CancellationToken ct)
    {
        var ss = start.ToString("F2", Inv);
        var t = seconds.ToString("F2", Inv);
        using var p = Process.Start(Psi(Ffmpeg, new[]
        {
            "-hide_banner", "-nostdin",
            "-ss", ss, "-t", t, "-i", distorted,
            "-ss", ss, "-t", t, "-i", reference,
            "-lavfi", "[0:v:0][1:v:0]ssim",
            "-f", "null", "-"
        }));
        if (p == null)
        {
            return null;
        }

        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        await outTask;
        var m = Regex.Matches(err, @"All:([0-9.]+)");
        return m.Count > 0 && double.TryParse(m[^1].Groups[1].Value, NumberStyles.Float, Inv, out var v) ? v : null;
    }
}
