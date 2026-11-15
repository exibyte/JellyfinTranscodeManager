using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TranscodeManager;

/// <summary>Persists jobs as JSON in the Jellyfin data directory.</summary>
public class JobStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _file;
    private List<TranscodeJob> _jobs = new();

    public JobStore(IApplicationPaths paths)
    {
        var dir = Path.Combine(paths.DataPath, "transcodemanager");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "jobs.json");
        try
        {
            if (File.Exists(_file))
            {
                _jobs = JsonSerializer.Deserialize<List<TranscodeJob>>(File.ReadAllText(_file)) ?? new();
            }
        }
        catch
        {
            _jobs = new();
        }
    }

    public List<TranscodeJob> All()
    {
        lock (_lock)
        {
            return _jobs.OrderByDescending(j => j.CreatedUtc).ToList();
        }
    }

    public TranscodeJob? Get(Guid id)
    {
        lock (_lock)
        {
            return _jobs.FirstOrDefault(j => j.Id == id);
        }
    }

    public void Add(TranscodeJob job)
    {
        lock (_lock)
        {
            _jobs.Add(job);
            SaveLocked();
        }
    }

    public TranscodeJob? NextQueued()
    {
        lock (_lock)
        {
            return _jobs.Where(j => j.Status == JobStatus.Queued).OrderBy(j => j.CreatedUtc).FirstOrDefault();
        }
    }

    public Dictionary<Guid, JobStatus> ActiveByItem()
    {
        lock (_lock)
        {
            return _jobs
                .Where(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.PendingReview)
                .GroupBy(j => j.ItemId)
                .ToDictionary(g => g.Key, g => g.First().Status);
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        File.WriteAllText(_file, JsonSerializer.Serialize(_jobs, JsonOpts));
    }
}
