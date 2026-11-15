using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeManager;

/// <summary>Runs queued jobs, but only starts them inside the configured time window.</summary>
public class TranscodeWorker : BackgroundService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly JobStore _store;
    private readonly MediaTools _tools;
    private readonly ILibraryManager _library;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<TranscodeWorker> _log;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public TranscodeWorker(JobStore store, MediaTools tools, ILibraryManager library, IApplicationPaths paths, ILogger<TranscodeWorker> log)
    {
        _store = store;
        _tools = tools;
        _library = library;
        _paths = paths;
        _log = log;
    }

    /// <summary>One-shot override: process the queue now, ignoring the window, until it is empty.</summary>
    public volatile bool RunNow;

    public bool IsRunning => !_running.IsEmpty;

    public static (TimeSpan Start, TimeSpan End) Window()
    {
        var cfg = Plugin.Instance?.Configuration;
        var ok1 = TimeSpan.TryParse(cfg?.WindowStart, Inv, out var s);
        var ok2 = TimeSpan.TryParse(cfg?.WindowEnd, Inv, out var e);
        return (ok1 ? s : TimeSpan.FromHours(1), ok2 ? e : TimeSpan.FromHours(7));
    }

    public static bool InWindow(DateTime now)
    {
        var (s, e) = Window();
        var t = now.TimeOfDay;
        if (s == e)
        {
            return true; // equal = always on
        }

        return s < e ? t >= s && t < e : t >= s || t < e; // handles windows crossing midnight
    }

    public string StagingDir()
    {
        var d = Plugin.Instance?.Configuration.StagingDir;
        if (string.IsNullOrWhiteSpace(d))
        {
            d = Path.Combine(_paths.CachePath, "transcodemanager");
        }

        Directory.CreateDirectory(d);
        return d;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Crash recovery: anything left "Running" goes back to the queue.
        foreach (var j in _store.All().Where(j => j.Status == JobStatus.Running))
        {
            j.Status = JobStatus.Queued;
            j.Progress = 0;
            TryDelete(j.OutputPath);
        }

        _store.Save();

        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (RunNow || InWindow(DateTime.Now))
                {
                    var job = _store.NextQueued();
                    if (job != null)
                    {
                        await RunJobAsync(job, stop);
                        continue;
                    }

                    RunNow = false;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Transcode worker loop error");
                await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            }
        }
    }

    private List<string> BuildArgs(TranscodeJob job, string output)
    {
        var a = new List<string>
        {
            "-hide_banner", "-y", "-nostdin", "-nostats", "-progress", "pipe:1",
            "-i", job.SourcePath,
            "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?",
            "-c", "copy"
        };

        if (job.Encoder == "libx265")
        {
            a.AddRange(new[] { "-c:v", "libx265", "-preset", job.Preset, "-crf", job.Cq.ToString(Inv), "-x265-params", "log-level=error" });
        }
        else
        {
            // RTX 20xx (Turing): HEVC NVENC with B-frames and lookahead is supported.
            a.AddRange(new[]
            {
                "-c:v", "hevc_nvenc", "-preset", job.Preset, "-tune", "hq",
                "-rc", "vbr", "-cq", job.Cq.ToString(Inv), "-b:v", "0",
                "-spatial-aq", "1", "-temporal-aq", "1", "-rc-lookahead", "32", "-bf", "3"
            });
        }

        a.Add(output);
        return a;
    }

    private async Task RunJobAsync(TranscodeJob job, CancellationToken stop)
    {
        if (!File.Exists(job.SourcePath))
        {
            Fail(job, "Source file no longer exists.");
            return;
        }

        var output = Path.Combine(StagingDir(), $"{Path.GetFileNameWithoutExtension(job.SourcePath)}.{job.Id.ToString("N")[..8]}.mkv");
        job.OutputPath = output;
        job.Status = JobStatus.Running;
        job.StartedUtc = DateTime.UtcNow;
        job.Progress = 0;
        job.Error = null;
        _store.Save();

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _running[job.Id] = jobCts;
        var userCancelled = false;
        Process? proc = null;
        try
        {
            proc = Process.Start(MediaTools.Psi(_tools.Ffmpeg, BuildArgs(job, output)))
                   ?? throw new InvalidOperationException("Could not start ffmpeg");

            var tail = new Queue<string>();
            var errTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                {
                    lock (tail)
                    {
                        tail.Enqueue(line);
                        if (tail.Count > 15)
                        {
                            tail.Dequeue();
                        }
                    }
                }
            });

            using var reg = jobCts.Token.Register(() =>
            {
                try
                {
                    proc.Kill(true);
                }
                catch
                {
                    // already gone
                }
            });

            string? l;
            while ((l = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (l.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(l.AsSpan(12), NumberStyles.Integer, Inv, out var us)
                    && job.DurationSeconds > 0)
                {
                    job.Progress = Math.Clamp(us / 1_000_000d / job.DurationSeconds * 100d, 0, 99.9);
                }
            }

            await proc.WaitForExitAsync(CancellationToken.None);
            await errTask;

            if (jobCts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            if (proc.ExitCode != 0)
            {
                string msg;
                lock (tail)
                {
                    msg = string.Join("\n", tail);
                }

                throw new InvalidOperationException($"ffmpeg exited with {proc.ExitCode}:\n{msg}");
            }

            await VerifyAsync(job, output, stop);
            job.OutputBytes = new FileInfo(output).Length;
            job.Progress = 100;
            job.Status = JobStatus.PendingReview;
            job.FinishedUtc = DateTime.UtcNow;
            _log.LogInformation("Transcode finished: {Name} {Src} -> {Out} bytes", job.Name, job.SourceBytes, job.OutputBytes);
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            userCancelled = job.Status == JobStatus.Cancelled;
            if (!userCancelled)
            {
                // server shutting down: requeue
                job.Status = JobStatus.Queued;
                job.Progress = 0;
            }
        }
        catch (Exception ex)
        {
            TryDelete(output);
            Fail(job, ex.Message);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
            proc?.Dispose();
            _store.Save();
        }
    }

    private async Task VerifyAsync(TranscodeJob job, string output, CancellationToken ct)
    {
        var src = await _tools.ProbeAsync(job.SourcePath, ct) ?? throw new InvalidOperationException("Cannot probe source.");
        var dst = await _tools.ProbeAsync(output, ct) ?? throw new InvalidOperationException("Cannot probe output.");
        if (dst.VideoCodec != "hevc")
        {
            throw new InvalidOperationException($"Output video codec is '{dst.VideoCodec}', expected hevc.");
        }

        if (Math.Abs(dst.Duration - src.Duration) > Math.Max(2, src.Duration * 0.01))
        {
            throw new InvalidOperationException($"Duration mismatch: source {src.Duration:F1}s, output {dst.Duration:F1}s.");
        }

        if (dst.AudioStreams != src.AudioStreams || dst.SubtitleStreams != src.SubtitleStreams)
        {
            throw new InvalidOperationException(
                $"Stream count mismatch: audio {src.AudioStreams}->{dst.AudioStreams}, subs {src.SubtitleStreams}->{dst.SubtitleStreams}.");
        }
    }

    public bool Cancel(Guid id)
    {
        var job = _store.Get(id);
        if (job == null || job.Status is not (JobStatus.Queued or JobStatus.Running))
        {
            return false;
        }

        job.Status = JobStatus.Cancelled;
        job.FinishedUtc = DateTime.UtcNow;
        if (_running.TryGetValue(id, out var cts))
        {
            cts.Cancel();
        }

        _store.Save();
        return true;
    }

    /// <summary>Replace the original with the new file (always as .mkv next to the original).</summary>
    public async Task AcceptAsync(TranscodeJob job)
    {
        if (job.Status != JobStatus.PendingReview || job.OutputPath == null || !File.Exists(job.OutputPath))
        {
            throw new InvalidOperationException("Job is not awaiting review or the output file is missing.");
        }

        var output = job.OutputPath;
        var dest = Path.ChangeExtension(job.SourcePath, ".mkv");
        var tmp = dest + ".tm-tmp";

        await Task.Run(() =>
        {
            File.Copy(output, tmp, true);   // copy first: staging may be on another volume
            File.Delete(job.SourcePath);    // only after the copy succeeded
            File.Move(tmp, dest, true);
            File.Delete(output);
        });

        job.OutputPath = dest;
        job.Status = JobStatus.Accepted;
        job.FinishedUtc = DateTime.UtcNow;
        _store.Save();

        _ = Task.Run(async () =>
        {
            try
            {
                await _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Library refresh after accept failed");
            }
        });
    }

    public void Reject(TranscodeJob job)
    {
        if (job.Status != JobStatus.PendingReview)
        {
            throw new InvalidOperationException("Job is not awaiting review.");
        }

        TryDelete(job.OutputPath);
        job.Status = JobStatus.Rejected;
        job.FinishedUtc = DateTime.UtcNow;
        _store.Save();
    }

    private void Fail(TranscodeJob job, string error)
    {
        job.Status = JobStatus.Failed;
        job.Error = error;
        job.FinishedUtc = DateTime.UtcNow;
        _log.LogError("Transcode failed for {Name}: {Error}", job.Name, error);
        _store.Save();
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
