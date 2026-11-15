using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeManager;

/// <summary>Finds library videos by codec and size-per-hour using Jellyfin's own library data.</summary>
public class CandidateService
{
    private const double AssumedSavings = 0.45;

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    private readonly JobStore _store;
    private readonly ILogger<CandidateService> _log;

    public CandidateService(ILibraryManager library, IMediaSourceManager mediaSources, JobStore store, ILogger<CandidateService> log)
    {
        _library = library;
        _mediaSources = mediaSources;
        _store = store;
        _log = log;
    }

    public Candidate? Describe(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return null;
        }

        var video = _mediaSources.GetMediaStreams(item.Id)
            .FirstOrDefault(s => s.Type == MediaStreamType.Video
                                 && !string.Equals(s.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(s.Codec, "png", StringComparison.OrdinalIgnoreCase));
        if (video == null)
        {
            return null;
        }

        long size;
        try
        {
            size = new FileInfo(item.Path).Length;
        }
        catch
        {
            return null;
        }

        var seconds = (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
        if (seconds <= 0)
        {
            return null;
        }

        var gb = size / 1024d / 1024d / 1024d;
        var name = item is Episode e
            ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00} - {e.Name}"
            : item.Name;

        return new Candidate
        {
            ItemId = item.Id,
            Name = name,
            Path = item.Path,
            Codec = video.Codec ?? string.Empty,
            Width = video.Width ?? 0,
            Height = video.Height ?? 0,
            SizeBytes = size,
            SizeGb = Math.Round(gb, 2),
            DurationMinutes = Math.Round(seconds / 60d, 1),
            GbPerHour = Math.Round(gb / (seconds / 3600d), 2),
            EstSavingsGb = Math.Round(gb * AssumedSavings, 2)
        };
    }

    public List<Candidate> Find(string codec, double minGbPerHour, double minSizeGb, double minDurMin, double maxDurMin, string? search)
    {
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true
        });

        var active = _store.ActiveByItem();
        var result = new List<Candidate>();
        foreach (var item in items)
        {
            try
            {
                var c = Describe(item);
                if (c == null)
                {
                    continue;
                }

                if (!string.Equals(c.Codec, codec, StringComparison.OrdinalIgnoreCase)
                    || c.GbPerHour < minGbPerHour
                    || c.SizeGb < minSizeGb
                    || c.DurationMinutes < minDurMin
                    || (maxDurMin > 0 && c.DurationMinutes > maxDurMin))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(search)
                    && !c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !c.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (active.TryGetValue(c.ItemId, out var st))
                {
                    c.JobStatus = st.ToString();
                }

                result.Add(c);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping {Path}", item.Path);
            }
        }

        return result;
    }
}
