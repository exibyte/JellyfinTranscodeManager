using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TranscodeManager.Api;

[ApiController]
[Route("TranscodeManager")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class TranscodeController : ControllerBase
{
    private readonly CandidateService _candidates;
    private readonly JobStore _store;
    private readonly TranscodeWorker _worker;
    private readonly MediaTools _tools;
    private readonly ILibraryManager _library;

    public TranscodeController(CandidateService candidates, JobStore store, TranscodeWorker worker, MediaTools tools, ILibraryManager library)
    {
        _candidates = candidates;
        _store = store;
        _worker = worker;
        _tools = tools;
        _library = library;
    }

    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var (s, e) = TranscodeWorker.Window();
        return Ok(new
        {
            InWindow = TranscodeWorker.InWindow(DateTime.Now),
            RunNow = _worker.RunNow,
            Running = _worker.IsRunning,
            WindowStart = s.ToString(@"hh\:mm"),
            WindowEnd = e.ToString(@"hh\:mm"),
            ServerTime = DateTime.Now.ToString("HH:mm")
        });
    }

    [HttpPost("RunNow")]
    public ActionResult SetRunNow([FromQuery] bool enable = true)
    {
        _worker.RunNow = enable;
        return NoContent();
    }

    [HttpGet("Candidates")]
    public ActionResult GetCandidates(
        [FromQuery] string codec = "h264",
        [FromQuery] double minGbPerHour = 0,
        [FromQuery] double minSizeGb = 0,
        [FromQuery] double minDurationMin = 0,
        [FromQuery] double maxDurationMin = 0,
        [FromQuery] string? search = null,
        [FromQuery] string sort = "size",
        [FromQuery] int limit = 500)
    {
        var list = _candidates.Find(codec, minGbPerHour, minSizeGb, minDurationMin, maxDurationMin, search);
        IEnumerable<Candidate> ordered = sort switch
        {
            "gbph" => list.OrderByDescending(c => c.GbPerHour),
            "savings" => list.OrderByDescending(c => c.EstSavingsGb),
            _ => list.OrderByDescending(c => c.SizeBytes)
        };
        return Ok(new { Total = list.Count, Items = ordered.Take(Math.Clamp(limit, 1, 5000)).ToList() });
    }

    [HttpPost("Queue")]
    public ActionResult Enqueue([FromBody] QueueRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        var active = _store.ActiveByItem();
        var added = 0;
        foreach (var id in req.ItemIds.Distinct())
        {
            if (active.ContainsKey(id))
            {
                continue;
            }

            var item = _library.GetItemById(id);
            var c = item == null ? null : _candidates.Describe(item);
            if (c == null)
            {
                continue;
            }

            _store.Add(new TranscodeJob
            {
                ItemId = c.ItemId,
                Name = c.Name,
                SourcePath = c.Path,
                SourceBytes = c.SizeBytes,
                DurationSeconds = c.DurationMinutes * 60d,
                Encoder = cfg.Encoder,
                Preset = req.Preset ?? cfg.Preset,
                Cq = req.Cq ?? cfg.Cq
            });
            added++;
        }

        return Ok(new { Added = added });
    }

    [HttpGet("Jobs")]
    public ActionResult GetJobs() => Ok(_store.All());

    [HttpPost("Jobs/{id}/Cancel")]
    public ActionResult Cancel(Guid id) => _worker.Cancel(id) ? NoContent() : BadRequest("Job cannot be cancelled.");

    [HttpPost("Jobs/{id}/Accept")]
    public async Task<ActionResult> Accept(Guid id)
    {
        var job = _store.Get(id);
        if (job == null)
        {
            return NotFound();
        }

        try
        {
            await _worker.AcceptAsync(job);
            return NoContent();
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("Jobs/{id}/Reject")]
    public ActionResult Reject(Guid id)
    {
        var job = _store.Get(id);
        if (job == null)
        {
            return NotFound();
        }

        try
        {
            _worker.Reject(job);
            return NoContent();
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Lossless frame from the original or the new file at second t.</summary>
    [HttpGet("Jobs/{id}/Frame")]
    [Produces("image/png")]
    public async Task<ActionResult> Frame(Guid id, [FromQuery] string which = "source", [FromQuery] double t = 60, CancellationToken ct = default)
    {
        var job = _store.Get(id);
        if (job == null)
        {
            return NotFound();
        }

        var path = which == "output" ? job.OutputPath : job.SourcePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        var bytes = await _tools.FrameAsync(path, t, ct);
        return bytes == null ? StatusCode(StatusCodes.Status500InternalServerError, "ffmpeg could not extract a frame") : File(bytes, "image/png");
    }

    /// <summary>SSIM over a 2-minute sample from the middle of the file.</summary>
    [HttpPost("Jobs/{id}/Ssim")]
    public async Task<ActionResult> Ssim(Guid id, CancellationToken ct)
    {
        var job = _store.Get(id);
        if (job?.OutputPath == null || !System.IO.File.Exists(job.OutputPath))
        {
            return NotFound();
        }

        var start = Math.Max(0, job.DurationSeconds / 2 - 60);
        var v = await _tools.SsimAsync(job.OutputPath, job.SourcePath, start, 120, ct);
        if (v == null)
        {
            return BadRequest("SSIM computation failed.");
        }

        job.Ssim = Math.Round(v.Value, 4);
        _store.Save();
        return Ok(new { Ssim = job.Ssim });
    }
}
