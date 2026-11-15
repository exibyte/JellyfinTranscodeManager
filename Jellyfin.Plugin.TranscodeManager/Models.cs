using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TranscodeManager;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    Queued,
    Running,
    PendingReview,
    Accepted,
    Rejected,
    Failed,
    Cancelled
}

public class TranscodeJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string? OutputPath { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public double Progress { get; set; }
    public long SourceBytes { get; set; }
    public long OutputBytes { get; set; }
    public double DurationSeconds { get; set; }
    public string Encoder { get; set; } = "hevc_nvenc";
    public string Preset { get; set; } = "p7";
    public int Cq { get; set; } = 24;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string? Error { get; set; }
    public double? Ssim { get; set; }
}

public class Candidate
{
    public Guid ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Codec { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public double DurationMinutes { get; set; }
    public double SizeGb { get; set; }
    public double GbPerHour { get; set; }
    public double EstSavingsGb { get; set; }
    public string? JobStatus { get; set; }
}

public class QueueRequest
{
    public List<Guid> ItemIds { get; set; } = new();
    public int? Cq { get; set; }
    public string? Preset { get; set; }
}
