# Unity Video Streamer

Unity UPM plugin that streams a rendered `Camera` view using the project's
frame protocol. The current Android backend uses MediaCodec; UDP, TCP, and USB
transports can be layered underneath the same frame protocol.

## Features

- Android H.264/HEVC encoding through `MediaCodec`.
- Captures a Unity `Camera` into a `RenderTexture` with `AsyncGPUReadback`.
- Fragments packets with the same `UdpFramer` layout used by the Android and PC
  receivers.
- Supports IDR requests from receivers and echoes latency probes.
- UPM package layout modeled after `com.kadu999.device-link`.

## Setup

1. Clone or reference this repository as a Unity package:
   - Package Manager -> Add package from git URL
   - or add `"com.videostream.unity-video-streamer": "https://github.com/kadu999/UnityVideoStreamer.git"`
     to `Packages/manifest.json`.
2. Build the Android JAR once:

   ```powershell
   cd Plugins/Android/java-src
   .\build-android-jar.ps1 -AndroidSdk "C:\Users\90683\AppData\Local\Android\Sdk"
   ```

3. In Unity, create a `Unity Video Streamer` object from
   `GameObject -> Video Stream -> Unity Video Streamer`.
4. Assign the camera that should be streamed and set the receiver IP/port.

## Compatible Receivers

The plugin emits the existing `FrameProtocol` packets:

```text
[FrameHeader 18 bytes][H.264/HEVC AnnexB NALUs]
```

Each large packet is split into UDP fragments with the same 10-byte header as
the existing `UdpFramer`. The receiver should be one of:

- Android `gateway` app.
- `windows/main.py` PC receiver.

## Platform Notes

- The encoder backend currently targets Android builds.
- `UnityVideoStreamer` uses a dedicated camera render texture; the source camera
  no longer renders directly to screen while streaming. Use a second display
  camera or a UI `RawImage` showing `RenderTexture` if live preview is needed.
- Default packet size, ports, and flags match `StreamConfig`/`FrameProtocol` in
  the companion video-streaming repository.
