# System Audio Recorder

A small, local Windows desktop recorder: **Start Recording**, **Pause/Resume**,
**Stop & Save MP3**, an optional automatic-stop timer, elapsed recorded time,
and a live sample-driven level history.
It records playback, never your microphone. No account, telemetry, uploads,
background startup, or automatic recording.

## Why I built this

I sometimes want to turn audio playing on my computer into text, then use that
transcript as context for AI tools: to summarize it, ask questions, or put the
information to work. I built this app to make the recording step simple:
capture system audio and save it as an MP3, ready for a separate transcription
tool.

This app only records audio. It does not transcribe, integrate with AI services,
or upload content.

## Run

Requires Windows 11, or a Windows 10 edition/version supported by .NET 10, with
the Windows Media Foundation components available. Build a portable release
using the instructions below, or extract a portable ZIP supplied to you and
open `SystemAudioRecorder.exe` in that folder. For a local versioned build:

```text
artifacts\publish\win-x64-1.0.4\SystemAudioRecorder.exe
```

The published folder is self-contained: no separate .NET installation is needed.
Keep the entire folder together. It runs on x64 Windows, and on Windows 11 ARM64
with x64 emulation. Source builds can also target `win-arm64`.

1. Select a playback device. The current default multimedia playback device is
   selected initially. Refresh after connecting or changing devices.
2. Click **Start Recording**, then play audio through that device.
3. **Pause** excludes paused audio and time; **Resume** continues the same file.
4. **Stop & Save** opens a save dialog. MP3 is stereo, 48 kHz, 320 kbps.
5. Cancelling the save dialog or export keeps the recording under **Unsaved
   recordings**. Retry **Save MP3...**, or explicitly confirm **Discard...**.

Closing during capture offers stop-and-save, stop-and-keep-recovery, or cancel.
Closing after a cancelled save keeps unsaved audio for the next launch. Only one
instance can run in a Windows login session.

## Optional Stop after timer (1.0.3)

Before starting a recording, enable **Stop after** and enter hours, minutes and
seconds. The supported range is **00:00:01 through 24:00:00**, inclusive.
Minutes and seconds must each be 0-59. Invalid or zero durations show an inline
error and disable Start Recording. The timer is **off by default**; when off,
the duration fields are ignored and recording behaves as before.

This timer uses **elapsed clock time, including pauses**, not recorded audio
duration. For example, a 10-minute timer with 3 minutes paused stops after
10 elapsed minutes and saves approximately 7 minutes of audio. The separate
**Auto stop in** countdown continues while paused; **RECORDED AUDIO** does not.
The chosen duration is fixed for that recording, starts when capture
successfully starts, and cannot be edited while recording. Each new recording
gets a fresh countdown.

At expiry, capture stops even if paused, the recovery file is finalized, and
the usual **Save recording as MP3** dialog opens. There is **no automatic save
destination**. If you are away, the dialog can wait while the audio remains
recoverable on disk. Cancelling the dialog/export retains it under Unsaved
recordings. If another operation or close-confirmation dialog is open, capture
still stops; the save prompt waits until that operation/dialog finishes.
Choosing to close and keep recovery instead leaves the audio for next launch.

Timing uses a monotonic clock on the capture worker, not the computer's
time-of-day or accumulated UI ticks. System-clock changes and delayed UI ticks
do not restart or extend the deadline. The worker checks the actual deadline
on its polling loop and between audio packets, so a frozen UI does not keep
capturing just because the displayed countdown is delayed. This is not a
sample-exact real-time alarm: OS scheduling, a stalled audio/disk driver, or
sleep can delay processing; the deadline is checked again as soon as the worker
can run. The timer does not wake the computer or prevent sleep.

Manual stop, capture failure, or closing ends that recording's timer. Competing
manual/timed stops are claimed once, and an old recording cannot stop a later
one. Diagnostics include `timer.configured`, `timer.started`, `timer.expired`,
`timer.cancelled`, and the `timer_elapsed` stop reason with the recording ID;
countdown ticks are not logged.

## Capture constraints

- Captures the mixed playback of apps routed through **one selected output
  endpoint**. This is not automatic mixing of every distinct speaker, HDMI,
  Bluetooth, or virtual endpoint. The selected endpoint is fixed during capture;
  changing Windows' default output does not retarget an active recording.
- Use headphones for a quiet room. Recording while output is muted depends on
  the device and driver; do not rely on it. Playback volume can affect the result.
- DRM/protected content, exclusive-mode apps, unusual drivers, and remote desktop
  audio routing may prevent capture. Follow applicable recording permissions.
- Windows' shared audio engine converts native float/PCM, multichannel audio and
  sample rates to stereo 16-bit/48 kHz using its channel matrixer and high-quality
  resampler. Unsupported drivers/formats fail visibly, rather than falling back
  to a microphone.
- Silence occupies real recorded time. The live timer can trail capture by about
  250 ms while waiting for audio packets; final duration includes trailing silence.
  A driver-reported discontinuity is shown as a warning; missing audio becomes
  silence, not recovered sound. Disconnects and invalid timestamps stop capture
  with an error and retain already-written audio.
- MP3 uses **Windows Media Foundation**, not an external executable or bundled
  LAME DLL. Windows N/KN or stripped-down Windows installations may lack the
  media components. In that case export reports the error and retains the PCM;
  install the appropriate Windows Media Feature Pack yourself before retrying.
- MP3 introduces a small amount of codec delay/padding. Do not use it for
  sample-exact archival or timing measurements.

## Recovery and privacy

Audio is streamed to:

```text
%LOCALAPPDATA%\SystemAudioRecorder\Recovery
```

Recovery files are ordinary **unencrypted** `.pcm` files: signed little-endian
16-bit stereo, 48,000 frames/second, with no header. Treat them as sensitive audio.
They use approximately **691 MB per hour** (659 MiB). Ensure adequate free disk
space. There is no entire-recording RAM buffer and no WAV 4 GB size limit.

The capture worker owns audio COM objects and the file. It flushes regularly;
after a crash, the next launch discovers the existing files. The last buffered
fraction of a second can be lost on abnormal termination or power loss. Export
ignores an incomplete final PCM frame left by an interrupted write.

Export uses a unique temporary MP3 in the chosen destination directory, and only
replaces the destination after successful encoding. Cancel/failure preserves
the source and any previous destination. Successful export removes the recovery
copy; deletion errors are reported and leave the extra copy visible. Discard
requires explicit confirmation. There is no automatic recovery-file expiry.

To salvage a copy manually, import it as raw audio in an editor using the PCM
settings above. Do not rename it to `.wav` or `.mp3`: raw PCM has neither header.

## Build, test, publish

Requires Windows, PowerShell 5.1 or later, and the .NET **10.0.4xx SDK**
(`global.json`, allowing patch updates within that SDK feature band). NuGet restores
NAudio.Wasapi/NAudio.Core 2.2.1 plus test dependencies. No SDK installation scripts,
native encoder downloads, or system changes are performed.

From the repository root in PowerShell:

```powershell
dotnet build SystemAudioRecorder.slnx -c Release
dotnet test SystemAudioRecorder.slnx -c Release
dotnet run --project src\SystemAudioRecorder -c Release
.\publish.ps1 -ReleaseTag 1.0.4
```

`publish.ps1` tests the target architecture and publishes a folder plus ZIP under
`artifacts`. The command above creates
`artifacts\SystemAudioRecorder-win-x64-1.0.4.zip` and the executable folder shown
earlier. Existing output folders and ZIPs are **never overwritten**: for a
rebuild, move the old output yourself or choose a fresh label such as
`-ReleaseTag 1.0.4-local2`. The label names the output; the app version itself
comes from `Directory.Build.props`. Untagged publishing uses
`artifacts\publish\win-x64`.

```powershell
.\publish.ps1 -Runtime win-arm64 -ReleaseTag 1.0.4
```

ARM64 publishing runs ARM64 tests and therefore needs an ARM64-capable test
machine. Close the old app before opening a new version; the script never
stops or replaces running instances.

For redistribution, use `publish.ps1`, not a bare `dotnet publish` command:
the script includes the project's MIT license, copies the actual runtime-pack
licenses/notices, checks for missing notices and accidental audio/log/key files,
and creates a ZIP with a SHA-256 checksum. Publishing here means producing
**local files**, not uploading or creating a repository/release.

WPF is intentionally not trimmed. Windows provides the encoder; all other
application/runtime assemblies are in the publish directory. Dependency license
information is in `THIRD-PARTY-NOTICES.txt` and the .NET runtime notices.
Build source paths in symbols are mapped to a fixed synthetic directory, so
stack traces retain file/line information without disclosing the builder's
checkout location. Review artifacts before distributing them.

## Contributing

Keep changes focused and follow the existing C#/WPF patterns. Run the tests
above; audio tests use generated synthetic data and should not start live
capture or play sound. Test live device behavior separately only with audio you
have permission to record.

When reporting a problem, include the app version, Windows version, device type,
reproduction steps and relevant **reviewed/redacted** diagnostic logs. Do not
post recordings or recovery files unless you deliberately intend to share that
audio. The diagnostics section below explains what logs contain.

`.gitignore` excludes build/release output, editor settings, recordings, logs,
crash dumps and common credential files. Before sharing source, inspect
`git status --short` and `git ls-files`; ignore rules do not remove files already
tracked or protect files added with `git add -f`. Do not commit real audio,
personal logs, signing keys or local environment files.

## License

Original System Audio Recorder code is licensed under the **MIT License**:
see [LICENSE](LICENSE). Copyright (c) 2026 Marco Fogliatto.

You may use, modify and redistribute it, including commercially, provided you
retain the copyright and permission notice. It is provided without warranty.
This does not relicense dependencies: NAudio retains its MIT notices; bundled
.NET runtimes and their third-party components retain their own licenses and
notices; Windows Media Foundation is supplied and licensed by Windows. See
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and retain the additional
runtime notices included in portable releases. Development-only test tooling
is not included in those releases.

## Design and validation

- `SystemAudioRecorder.Core`: fixed PCM format, monotonic timestamp/segment
  timeline, audio-device frame positioning, bounded silence writes, recovery discovery.
- `SystemAudioRecorder`: WPF UI, dedicated single-owner WASAPI loopback worker,
  queued pause/resume/stop commands, cancellable transactional MP3 export.
- Tests use **synthetic audio only**: silence and tone MP3 encode/decode, bitrate
  and duration, Windows float/surround conversion, paused-time exclusion,
  packet alignment/overlap, bounded long-duration writes, recovery, export errors
  and cancellation. Timestamp-jitter and audio/wall-clock-drift tests require
  bit-exact preservation of continuous PCM samples. They never start a capture
  client or play sound.
- Diagnostics tests cover rotation/retention, bounded concurrent producers,
  exception formatting, disk/lock failures, flush/shutdown, session markers,
  export correlation and health aggregates. The invalid-device lifecycle test
  uses a deliberately nonexistent endpoint and fails before capture starts.
- Timer tests use an injectable monotonic clock for disabled mode, paused time,
  exact/late deadlines, clock changes, stop/expiry races, failed starts and fresh
  recording deadlines. An unshown WPF window checks timer defaults and validation
  without clicking Record or opening an audio device.

Synthetic conversion tests exercise Media Foundation, not a physical playback
driver. Building, passing tests, and opening the UI do **not** establish end-to-end
live loopback behavior on a particular device. Live recording, timer expiry and
device-unplug behavior need separate hardware testing with authorized audio.

### Audio quality fix in 1.0.1

Earlier capture code realigned every packet to its wall-clock timestamp, which
could insert zeros or remove samples at packet boundaries when the timestamp
jittered or the audio clock drifted. Version 1.0.1 places continuous packets by
their integer device-frame positions instead. Wall time is used for segment
boundaries, actual discontinuities, and periods with no incoming audio. Silence
is not injected into a continuously arriving stream merely to chase wall time.
This preserves genuine device-position gaps while avoiding artificial crackle.

The capture worker also releases the Windows packet before writing to disk, and
uses buffered periodic flushes rather than forcing a disk-cache flush during
capture. Stopping still performs a durable flush. MP3 export is now 320 kbps
(about 144 MB per hour) to reduce lossy compression artifacts; this does not
repair glitches already present in older recordings. No noise gate, denoiser,
or automatic gain processing alters the source audio.

### Local diagnostics in 1.0.2

Use **Open Logs Folder** in the window, or open:

```text
%LOCALAPPDATA%\SystemAudioRecorder\Logs
```

The UI shows whether local logging is active, unavailable, or dropping events.
Nothing is uploaded. Diagnostics never contain PCM samples, audio clips, or
waveform data. No credentials, environment-variable dumps, microphone data, or
other applications' activity are collected.

`recorder.log` contains UTF-8 JSON Lines (one JSON object per event).
`recorder.1.log` through `recorder.4.log` retain the previous files, newest first.
Each log is bounded to **2 MiB**, **five files / 10 MiB total**. A bounded queue
holds at most **512 events**. Records and exception text are size-limited;
each JSONL record is at most **48 KiB**, messages at most 2,048 characters, and
formatted exceptions at most 4,096 characters across 16 nested/aggregate
exceptions. Oversized values are truncated. About one eighth of queue capacity
is reserved for warnings/errors and durable lifecycle events; if that also
fills, these events can be dropped and are counted too. Queue saturation is
counted and reported rather than blocking audio capture. A single background writer owns rotation and
flushing. Lifecycle/fatal events request durable disk flushes on that writer;
capture does not wait for logging disk I/O. Ordinary records flush periodically.

The directory also contains a small `session-state.json` clean-exit marker and
the logger's lock file. The marker is durably written at startup and marked
clean only after a nonfatal, idle, zero-code exit and a successful log drain.
An unconfirmed previous exit is highlighted on the next launch. This is not
proof of a crash: forced termination, power loss, a log failure, or an incomplete
marker can all leave an unconfirmed exit. Versions older than 1.0.2 did not write
these markers, so the first diagnostic launch cannot explain their exits.

Events include:

- UTC timestamps, level, thread, app-session ID, recording ID, and export ID.
  Recovery filenames' generated GUIDs let you match an existing recording to
  events across restarts without logging its directory or chosen MP3 filename.
- App version, .NET runtime, OS version and architectures, start/exit, window
  close choices, recovered-file counts/sizes, and explicit discard decisions.
- Playback-device selection using a **session-salted anonymous reference**, not
  its raw endpoint ID or friendly name; native and requested formats, buffer
  size, startup time, pause/resume/stop requests and completions, and stop reason.
- Capture health every **10 seconds**, plus a final summary: frames/bytes,
  inserted silence, excluded paused frames, overlap trimming, discontinuities,
  device-frame gaps, delivery-gap counts/maxima, QPC-versus-device-frame offsets,
  packet backlog, command backlog, write/flush timings. These are cumulative
  aggregates, not per-packet logs. A delivery gap may simply mean silence; a
  device-frame gap or discontinuity is separate evidence.
- Independent process health every **15 seconds**: working/private/managed
  memory, GC pressure, CPU, threads/handles, thread-pool backlog, UI and capture
  heartbeat ages/current worker stage, and logger queue/drop counts. These can
  help locate hangs even when the capture worker is stuck. UI heartbeat age can
  also rise during a deliberately blocking operation.
- Export start/input size/periodic progress/completion/cancellation/failure,
  output size and elapsed time, plus errors with exception types, inner
  exceptions and stack traces (within the record limit).

User-home paths in diagnostic text are masked. Normal events avoid full personal
paths and chosen filenames. **Review logs before sharing**: OS/library exception
messages and stack traces can still contain file or system details outside the
masked home directory. Logs are local, unencrypted diagnostic files.

For troubleshooting, note the approximate time, version, selected device type,
and action. Reproduce only if you are comfortable recording the audio involved,
then save your recording and close normally. Copy the five `recorder*.log`
files and `session-state.json` into a support ZIP; do not include the `Recovery`
folder unless you explicitly intend to share recorded audio. Active files are
readable, but copying after closing avoids racing a rotation. Keep the logs soon
after an incident since retention is size-based, not unlimited.

Unhandled WPF dispatcher and AppDomain exceptions are reported and get a short
best-effort flush; they are **not swallowed** to keep an unsafe process running.
Unobserved Task exceptions are reported without changing .NET's default failure
semantics. Shutdown drains logs with a bounded timeout. Logging failures produce
a nonrecursive UI warning; the app does not falsely report successful logging.
Native crashes, fail-fast/stack-overflow/OOM, forced kills, OS shutdown and power
loss may prevent any final event or flush. These logs supplement, not replace,
Windows Error Reporting or a crash dump. Audio recovery remains independent.
