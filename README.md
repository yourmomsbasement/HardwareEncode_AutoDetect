# Jellyfin Hardware Acceleration Auto-Detect

A Jellyfin plugin that finds the GPU(s) in your server, **tests them with real ffmpeg jobs**, and writes
the settings that actually work into **Dashboard → Playback → Transcoding**.

It doesn't guess from a GPU model table. Every codec it turns on has passed a hardware
decode or encode run on your machine, using the same device setup Jellyfin itself uses.

## What it configures

| Jellyfin setting | How it's decided |
|---|---|
| Hardware acceleration (`nvenc` / `qsv` / `vaapi` / `amf` / `videotoolbox` / `rkmpp`) | Every candidate is probed and the highest scoring one wins (encoders weigh most). You can set a preferred accelerator; it's used if it works. |
| VA-API device / QSV device | The render node (`/dev/dri/renderD*`) that passed. |
| Enable hardware decoding for: H264, HEVC, MPEG2, VC1, VP8, VP9, AV1 | A small clip of each codec is made with a software encoder, then decoded through the hwaccel. The output goes through `hwdownload`, so a silent fallback to software decoding counts as a failure. |
| HEVC 10-bit, VP9 10-bit | 10-bit clips. |
| HEVC RExt 8/10-bit, HEVC RExt 12-bit | 4:2:2 and 4:4:4 at 10 bit, and 4:2:0/4:2:2/4:4:4 at 12 bit. Every layout has to pass. |
| Enable hardware encoding | `h264_*` hardware encoder test. |
| Allow encoding in HEVC / AV1 | `hevc_*` / `av1_*` encoder test, plus your preference. If the GPU can't encode a codec, its option is turned **off** so Jellyfin never falls back to slow software HEVC/AV1. |
| Intel low-power H.264 / HEVC encoders | `-low_power 1` encoder test (Intel QSV / VA-API only). |
| Enable tone mapping (OpenCL/CUDA), VPP tone mapping, VideoToolbox tone mapping | A synthetic HDR10 frame run through `tonemap_opencl`, `tonemap_cuda`, `tonemap_vaapi`/`vpp_qsv` or `tonemap_videotoolbox`. |

Settings the plugin doesn't manage (CRF, presets, throttling, paths and so on) are left as they are.

### Supported platforms

| OS | Accelerators probed |
|---|---|
| Linux (bare metal or Docker with `/dev/dri` and/or the NVIDIA runtime) | NVENC (CUDA), Intel QSV (on VA-API, iHD driver), VA-API (Intel/AMD/other), Rockchip MPP |
| Windows | NVENC, Intel QSV (D3D11), AMD AMF (D3D11) |
| macOS | VideoToolbox |

On Linux, GPUs are found through `/sys/class/drm/renderD*` (PCI vendor IDs), `/dev/nvidiactl` and `/dev/mpp_service`.
On Windows they're read from the display-adapter registry class. If nothing is found, the common defaults are probed anyway.

## When it runs

- **At startup, when the GPU or ffmpeg changed** (default). It keeps a fingerprint of the detected devices and the ffmpeg path and version, so new hardware or a Jellyfin/ffmpeg upgrade triggers a re-detect. Your manual tweaks are left alone otherwise.
- **At every startup**, or **manual only**.
- **On demand** from the plugin page: *Detect (preview only)* shows what would change without saving anything. *Detect & apply* writes the result.
- As a scheduled task (*Dashboard → Scheduled Tasks → Detect and apply hardware acceleration*). It has no default trigger, so you can add your own.

Before the first change, the plugin backs up the original values of every setting it manages. The **Restore original settings** button puts them back.

## Requirements

- Jellyfin **12.2 or newer** (.NET 10), including **13.0 unstable**. To build for 10.11, change `TargetFramework` to `net9.0` and the `Jellyfin.*` package versions to `10.11.*` in the `.csproj` and `build.yaml`.
- Use **jellyfin-ffmpeg**, which ships with the official packages and Docker image. Upstream ffmpeg lacks `tonemap_cuda`/`tonemap_opencl` and some of the other filters Jellyfin depends on, so those features will (correctly) test as unsupported.
- The GPU drivers / container device passthrough have to be set up already. The plugin detects what's usable; it doesn't install drivers.

## Building and installing

```bash
dotnet build -c Release
dotnet test
dotnet publish src/Jellyfin.Plugin.HardwareAutoDetect -c Release -o out
```

Copy `out/Jellyfin.Plugin.HardwareAutoDetect.dll` into a new folder under your Jellyfin `plugins` directory,
for example `/var/lib/jellyfin/plugins/HardwareAutoDetect_1.0.0.0/` or `/config/plugins/...` in Docker. Then restart Jellyfin.
You can also package it with [jprm](https://github.com/oddstr13/jellyfin-plugin-repository-manager) using `build.yaml`.

## Jellyfin plugin repository

After the first release has been published, add this repository in **Dashboard →
Plugins → Repositories**:

- Name: `Hardware Auto-Detect`
- URL: `https://github.com/yourmomsbasement/HardwareEncode_AutoDetect/releases/latest/download/manifest.json`

The plugin then appears in the catalog for compatible Jellyfin **12.2+** servers,
including **13.0 unstable**. Jellyfin filters catalog entries by the numeric server
version, not by a stable/unstable channel flag; the minimum ABI is `12.2.0.0`,
matching the Jellyfin packages used to compile the plugin.
It does not support Jellyfin 10.11 without the code and dependency changes described
above. This is a third-party repository; publishing it does not add the plugin to
Jellyfin's official repository.

### Publishing a release

The **Release Jellyfin repository** GitHub Actions workflow builds and tests the
plugin, publishes a ZIP containing its DLL, and attaches `manifest.json` to a
GitHub release. The manifest includes the plugin GUID, target ABI, version,
download URL, ZIP MD5 checksum, and earlier released versions. GitHub Actions
must be enabled and allowed to write repository contents.

1. Set the same new four-part version in `Directory.Build.props` and `build.yaml`.
   Update the changelog in `build.yaml` and commit to `main`.
2. Push the matching tag, for example:

   ```bash
   git tag v1.0.0.0
   git push origin v1.0.0.0
   ```

   Alternatively, run **Release Jellyfin repository** manually on `main` in the
   Actions tab. Existing versions are immutable; increment the version for later
   releases.
3. Confirm that the workflow succeeded and the release contains both the ZIP and
   `manifest.json` before adding the URL in Jellyfin.

To verify packaging locally after publishing the DLL:

```bash
python3 -m pip install PyYAML==6.0.3
python3 scripts/package_plugin.py
```

Generated files are under the ignored `artifacts/repository/` directory. The
catalog is generated from `build.yaml` and the exact ZIP being uploaded, so its
checksum always corresponds to the release asset. Do not hand-edit checksums.

### Unstable compatibility

Version 1.0.1.0 was tested against Jellyfin `master` commit
`aec42b345e1a7da25ee5492183f12209338df688` (server version 13.0.0).
The server loaded the plugin as active, accepted authenticated status and
detection-preview requests, and returned the plugin from its available-package
catalog using the generated manifest. The existing 23 plugin tests also passed.
GPU acceleration still depends on the server's drivers and jellyfin-ffmpeg.

The **Jellyfin unstable compatibility** workflow repeats the runtime and catalog
check on changes to `main`, pull requests, manual runs, and weekly against the
latest upstream `master`. Future unstable API changes can break compatibility;
check that workflow before upgrading. The smoke test only configures a fresh,
isolated test server and must never be run against an existing installation.

## API

All endpoints require an administrator.

| Method | Path | |
|---|---|---|
| `POST` | `/HardwareAutoDetect/Run?apply=false` | Run detection and return the report. `apply=true` writes the settings. |
| `GET` | `/HardwareAutoDetect/Report` | Last report (204 if never run). |
| `GET` | `/HardwareAutoDetect/Status` | `{ "HasBackup": bool }` |
| `POST` | `/HardwareAutoDetect/Restore` | Restore the pre-plugin settings. |

## Known limitations

- **VC-1** can't be tested because ffmpeg has no VC-1 encoder to make a sample. On Intel and NVIDIA it's enabled when MPEG-2 decoding works (you can turn this off). On other vendors it's left off.
- On macOS, VideoToolbox has a built-in software fallback, so its decode results are less strict than on other platforms.
- V4L2 (Raspberry Pi) is not probed. Jellyfin has deprecated it.

## Layout

```
src/Jellyfin.Plugin.HardwareAutoDetect/
  Detection/GpuEnumerator.cs          find GPUs (sysfs / registry)
  Detection/CandidatePlanner.cs       which accelerators to try, and picking the winner
  Detection/AcceleratorRecipe.cs      per-accelerator ffmpeg args (mirrors Jellyfin's EncodingHelper)
  Detection/SampleLibrary.cs          builds the test clips
  Detection/HardwareProber.cs         runs the decode/encode/tonemap jobs
  Detection/EncodingSettingsMapper.cs probe result -> EncodingOptions
  Detection/HardwareDetectionService.cs  orchestration, apply, backup/restore, report
  StartupDetectionService.cs          startup behaviour
  Api/HardwareAutoDetectController.cs REST API for the config page
  Configuration/configPage.html       dashboard page
tests/                                unit tests (fake ffmpeg) + real-ffmpeg integration test
```
