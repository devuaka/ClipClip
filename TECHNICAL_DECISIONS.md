# ClipClip technical decisions

## Stack

| Layer | Choice | Why |
|---|---|---|
| Language and UI | C# on .NET 10 (LTS), WPF | Native, GPU composited UI with no browser runtime. Rejected Electron and Tauri: extra processes and memory for an app that sits beside a game. |
| Capture | Windows.Graphics.Capture | Reads from the compositor. No injection, so anti-cheat has nothing to flag. Rejected OBS-style game hooks for that reason, and Desktop Duplication because it cannot capture one window and breaks on mode switches. |
| Encoding | libavcodec in process, FFmpeg 9.0 | One API over NVENC, AMF, Quick Sync and x264. Rejected piping to ffmpeg.exe because it forces every frame through the CPU. |
| Audio | WASAPI through NAudio's core wrappers | Gives per-packet QPC timestamps, which sync depends on. The higher level NAudio capture classes do not expose them. |
| Voice | Vosk, small English model | Offline, free, Apache 2.0, and works from any audio stream. Rejected SAPI (tied to Windows language packs) and whisper.cpp (far too heavy to run all the time). |
| Installer | Velopack | Per-user install, no admin rights, Start Menu entry, proper uninstall. |

## Recording pipeline

```
WGC frame (BGRA on GPU)
  -> CopySubresourceRegion to our texture
  -> ID3D11VideoProcessor: scale, BGRA to NV12 BT.709
  -> h264_nvenc / h264_amf take the D3D11 texture directly (zero copy)
     or staging readback for h264_qsv, libx264, h264_mf
  -> packets into PacketRing

WASAPI loopback + WASAPI mic
  -> swresample to 48 kHz stereo float
  -> TimelineBuffer, placed by QPC timestamp
  -> mixer 150 ms behind real time -> AAC -> PacketRing

Clip request -> ClipPlan (metadata only) -> remux to temp MP4 -> reopen, decode a frame -> rename
```

### Why the buffer holds encoded packets

Clips are remuxed, never re-encoded, so a 3 minute clip saves in well under a second and capture is never touched. Encoded video at 20 Mbps is about 2.5 MB/s, against 180 MB/s for raw 1080p60.

The ring is one preallocated block, so it does not fragment or put pressure on the GC. It lives in RAM, so nothing is written to disk during normal use. A delete-on-close mapped temp file takes over only when the buffer would be over 2 GB or a quarter of RAM, through the same pointer interface. Most buffers never touch the worst-case size because old packets are evicted by age and the write position wraps early when the real bitrate is low.

### Fixed canvas

The encoder's output size is decided once. Whatever the source does (game resolution change, fullscreen toggle, window resize, moving between desktop and game) is scaled into that canvas with letterboxing. The encoder keeps running and the buffer survives. Only changing a video setting restarts the encoder.

Each encoder instance is an "epoch". Streams from two encoder instances cannot share an MP4, so a clip uses the newest epoch only. After a settings change or a crash recovery, clips are short until the buffer refills.

### Timing

Everything is stamped on one clock, absolute QPC in microseconds, which is what WASAPI and WGC already use. Video ticks sit on a fixed grid, so output is constant frame rate: a tick with no new frame repeats the last one, and a missed tick leaves a timestamp gap rather than drifting from audio. Audio goes where its timestamp says, so an idle output device (no loopback packets) or a mic with a different clock cannot pull it out of sync. Jitter under 20 ms is ignored to avoid clicks.

Keyframes come once a second. Clips start on the keyframe at or before the requested start, so they run up to one second long, never short.

### Reliability

- A failed video session tears down and rebuilds after 1.5 s. Two quick failures on one encoder demote it to the next candidate (hardware, x264, Media Foundation).
- Clips are written to a temporary name, reopened, checked for duration, and a frame is decoded. Only then is the file renamed into place. Leftover temp files are removed at startup.
- Saving runs on its own thread and reads packets under a short lock. If a packet was evicted during a save, the save fails loudly.
- The audio path takes no shared locks, so stopping a source cannot deadlock with a packet in flight.
- A mic or output device going away is a normal state. The source drops to Disconnected and keeps retrying, and the clip simply has no mic audio.
- One debounce in the engine covers every trigger, so a key bounce or hotkey plus voice cannot produce two clips.

### Game detection

The detector never opens game processes for reading, because anti-cheat drivers watch for that. It uses the foreground window, the kernel process snapshot, `PROCESS_QUERY_LIMITED_INFORMATION` for the image path, and files on disk.

In order: custom list, Minecraft special case, known executables, store libraries (Steam, Epic, GOG, Xbox manifests), Windows' own game list, then evidence. Strong evidence is a game engine beside the exe (UnityPlayer.dll, Unreal paths and so on), which is enough for a windowed game. Weak evidence is a direct graphics or gamepad import, which also requires fullscreen. Browsers, media players, editors and launchers are excluded outright, and Electron apps never count.

A game stays active until its process exits. In game-only mode the buffer is kept for 2 minutes after a game closes, so a crash can still be clipped, and then released.

### Voice

A grammar containing only the phrase runs all the time because it is very cheap, but grammar mode reports 1.00 confidence for anything vaguely similar. Testing showed "flip the hat over" scoring 1.00. So each candidate is re-decoded from the last 4 seconds with the full vocabulary, and that decode supplies the real confidence used by the sensitivity setting. The microphone is only open while voice is enabled and recording is active.

Sensitivity at maximum skips the second stage. That is the escape hatch for accents the small model handles badly.

## Choices worth knowing about

- **H.264 only.** It plays everywhere, including WPF's own player and Discord. HEVC and AV1 would shrink files but break both.
- **One mixed audio track.** Simple and universally playable, but volume changes apply going forward only and the mix cannot be redone later. Because the mic is baked into buffered audio, turning "Include my microphone" off clears the buffer, or earlier mic audio could still reach a clip.
- **Window capture for games.** Notifications and other apps stay out of clips. It falls back to monitor capture if a window delivers no frames for 3 seconds.
- **Default output is 1080p60.** Sensible files and memory. Native is one setting away.
- **The clip chime can land in the next clip.** It plays through system audio, which is being recorded. Turn off "Clip sound" if that matters.
- **The main window is destroyed when closed to tray**, not hidden, so nothing renders and thumbnails are freed while a game runs.

## Measured on the development PC

Ryzen with 32 threads, RTX 5070, 2560x1440 at 60 Hz, 1080p60 High, NVENC.

| Measure | Result |
|---|---|
| CPU while recording in tray | 0.22% of all cores |
| Working set while recording in tray | 281 MB |
| NVENC encoder load | 9% |
| FPS cost, GPU-bound renderer | 5.7% (about 80 to 76.5 fps), still recording 59.6 fps |
| Video to system audio | 1.4 to 12.7 ms mean across runs, widest range -2.4 to 26.5 ms |
| System audio to mic through a virtual cable | 66 to 71 ms, stable within about 10 ms. Includes the cable's own latency, which was not measured separately. |
| Memory growth after buffer fills | 1.2 to 9.6 MB over 40 s |
| Voice detection, synthetic speech | 8 of 8 phrases, 0 of 22 near misses |

The FPS test is a worst case: an uncapped renderer at full GPU load, with the capture and compositor work competing for it. A game with GPU headroom or a frame cap has less to lose.

## Known limits

- **True exclusive fullscreen** with fullscreen optimizations disabled was not tested. Window capture falls back to monitor capture, but very old D3D9 titles may capture black. Borderless and normal fullscreen are the supported cases.
- **Intel Quick Sync is untested.** The dev PC has no Intel GPU. The path is the same system memory NV12 path x264 uses, and it fell back correctly when QSV was unavailable.
- **HDR displays** are captured as 8-bit SDR by the compositor, so highlights can look flat.
- **Voice is English only** and was tested with synthetic voices and through a virtual cable, not with a range of real speakers. Expect to tune sensitivity.
- **Windows 10 requires version 2004 or later.** The yellow capture border can only be removed on newer builds, and only Windows 11 was tested.
- **A path with non-ASCII characters** in the install location may stop Vosk loading its model. Voice would show as unavailable and everything else keeps working.
- **After a crash recovery or settings change** clips only reach back to the restart.

## Licensing

The bundled FFmpeg is the GPL build, because it includes x264, the best software fallback. Using it means ClipClip as distributed falls under GPL terms: you can share it freely, but must offer source. To ship closed source, switch `third_party/ffmpeg` to the LGPL build. No code changes are needed, since software encoding then falls back to Media Foundation automatically. H.264 and AAC are also patent-encumbered, which matters for commercial distribution and not for personal use.

The installer is not code signed, so Windows SmartScreen warns on first run. Removing that needs a paid certificate.
