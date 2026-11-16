# Jellyfin Transcode Manager (H.264 -> H.265)

Targets Jellyfin **10.11.x** and above -> tested with 12.3.0.0 (net9.0). Change the two `Jellyfin.*` package versions in the csproj to match your server exactly, otherwise Jellyfin shows the plugin as "Not supported".

## Build (no local SDK needed)
    ./build.sh
Produces `dist/TranscodeManager_0.1.0.0/` (dll + meta.json).

## Install when building yourself:
Copy that folder to `<jellyfin-config>/plugins/` (Docker: the folder mounted at `/config`, so `/config/plugins/TranscodeManager_0.1.0.0/`), then restart Jellyfin.
Open **Dashboard > Transcode Manager** (also in the admin sidebar).

## Jellyfin in Docker: required for your nvidia card (tested with RTX 2080)
- GPU passthrough: `runtime: nvidia` (or `deploy.resources.reservations.devices` with `driver: nvidia, capabilities: [gpu]`) and `NVIDIA_DRIVER_CAPABILITIES=all`.
- Set `TZ=Europe/Berlin` (or yours) - the nightly window uses the server's local time.
- Media mounts must be **read-write** (accepting a job replaces the original).
- Verify: `docker exec jellyfin /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -encoders | grep hevc_nvenc`
- Staging dir: default is Jellyfin's cache dir. Make sure it has space for a few large files and is NOT inside a library.

## First test (safe)
1. Candidates tab: leave Min GB/hour at 3, Search.
2. Tick ONE short episode, Queue selected.
3. Queue & review tab: **Run queue now** (bypasses the window).
4. When it reaches *PendingReview*: Compare (slider + SSIM), then Accept or Reject.
Originals are never touched until you press Accept.

## Notes / limits
- State is a JSON file (`<jellyfin-data>/transcodemanager/jobs.json`), no Redis.
- Output is always `.mkv`. If the original was `.mp4` the extension changes, so Jellyfin sees a new item (watch state of that item can be lost). `.mkv` originals are replaced in place.
- mov_text subtitles from MP4 can't be copied into MKV; such jobs fail verification with a clear error.
- Jobs started inside the window always run to completion.


## Install - when using the release
1. In Jellyfin: Dashboard → Plugins → Catalog → ⚙ (settings) → Add repository
2. Paste: https://raw.githubusercontent.com/exibyte/JellyfinTranscodeManager/main/manifest.json
3. Back in Catalog, install "Transcode Manager", then restart Jellyfin.
4. Open Dashboard → Transcode Manager.

Requires Jellyfin 10.11+ and an NVIDIA GPU with NVENC (or use the libx265 CPU encoder).
**Warning:** Accept permanently deletes the original file. Back up first.
