# AcePlayer

A lightweight Windows player for **Ace Stream** live broadcasts that stays as close to the real
live edge as possible — and, just as importantly, recovers from network hiccups instead of
silently drifting further and further behind.

## Why this exists

I watch live sports over Ace Stream. My HTPC sits on Wi‑Fi, and with the usual players — VLC, the
official Ace Stream player — I kept hitting the same annoyance: playback would briefly stall, then
resume, but **every stall pushed me further behind the actual broadcast and the lag only ever
accumulated**. After a while I'd be tens of seconds behind real time, and the only fix was to stop
and restart the stream.

For a lot of people that's perfectly acceptable — being a few seconds, or even a minute, behind
live simply doesn't matter to them. For me, watching a match, it did. So I wrote this.

AcePlayer has one design goal:

> **Always show the freshest frames, smoothly, and never silently drift behind live.**

When it genuinely falls behind (a real network stall), it catches back up to the live edge on its
own — on a threshold you choose — rather than quietly accumulating latency forever.

## What it does

- Decodes the Ace Stream MPEG‑TS feed **directly via FFmpeg**, so it has full control over
  buffering and the "live edge" — something off‑the‑shelf players don't hand you.
- **Deinterlacing** (bwdif) for broadcast 1080i / 576i content.
- **Smooth, low‑latency playout**: a small cushion absorbs jitter and presentation is clock‑driven,
  so it neither stutters nor accumulates delay in normal operation.
- **Automatic jump‑to‑live** on a configurable lag threshold (Off / 1–8 s). Below the threshold it
  leaves playback alone — no constant re‑syncing; once accumulated stall time crosses it, it snaps
  back to live in a single move.
- **Closes the ad window** that the ad‑supported Ace engine pops open when a stream starts.
- **Fullscreen** (double‑click or F11) with auto‑hiding controls and cursor.
- **Portable single `.exe`** (~5 MB) with a trimmed FFmpeg build embedded — nothing to install and
  no DLLs to ship alongside it.
- Registers as the `acestream://` protocol handler and for `.acelive` / `.acestream` files
  (current user only, no administrator rights).
- Remembers the last source and the live threshold between runs.

## Requirements

- Windows 10 / 11 (x64)
- The [Ace Stream](https://acestream.org/) engine installed and running (it listens on
  `127.0.0.1:6878`)
- .NET Framework 4.8

## Usage

Paste any of these into the address bar and press **Play**:

- a 40‑hex content id — `b28db77c…`
- an `acestream://<id>` link
- `infohash:<hash>`
- a direct `http(s)://` MPEG‑TS URL, or a local file path

Click the gear (⚙) once to register AcePlayer as the Ace Stream handler; after that, clicking an
`acestream://` link anywhere opens it here.

The **auto‑live** control sets how much accumulated stall time it will tolerate before jumping back
to the live edge. Keep in mind that the Ace engine's own *live buffer* setting is the real floor on
latency — the player can't be fresher than the engine serves.

## How it works (short version)

```
Ace engine (HTTP :6878) ──MPEG-TS──▶ FFmpeg demux + decode (H.264/HEVC, AAC/MP2/AC3)
                                        ├─ video ─▶ bwdif deinterlace ─▶ BGRA ─▶ frame buffer
                                        │                                   │
                                        │                       clock-driven presenter (WPF)
                                        └─ audio ─▶ resample ─▶ PCM ─▶ NAudio (the real-time clock)
```

The presenter keeps a small cushion and plays by a wall‑clock; accumulated freeze time is measured
as "how far behind live we are", and crossing the threshold triggers a clean catch‑up to the live
edge on the same connection (no reconnect, no stutter).

## Build

Open `AcePlayer.sln` in Visual Studio 2022, or:

```
dotnet build AcePlayer/AcePlayer.csproj -c Release -p:Platform=x64
```

Targets `net48` / x64. NuGet restores **FFmpeg.AutoGen**, **NAudio** and **Costura.Fody** (which
merges the managed dependencies into the single exe).

The bundled native FFmpeg is a trimmed 6.1 build (H.264/HEVC/MPEG‑2 video, AAC/MP2/MP3/AC3 audio,
mpegts/hls demux, bwdif) embedded as resources and extracted at first run — prebuilt DLLs are in
`AcePlayer/native/`. To rebuild them from scratch, see [`docs/BUILD_FFMPEG.md`](docs/BUILD_FFMPEG.md).

## License

AcePlayer is licensed under the **GNU General Public License v3.0** — see [`LICENSE`](LICENSE).

It bundles a trimmed **FFmpeg** build configured with `--enable-gpl` (FFmpeg is *GPL v2 or later*),
so the distributed whole is GPL. The FFmpeg build recipe is in
[`docs/BUILD_FFMPEG.md`](docs/BUILD_FFMPEG.md); FFmpeg source is available from
[ffmpeg.org](https://ffmpeg.org/).
