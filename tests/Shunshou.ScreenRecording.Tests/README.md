# Screen recording focused verification

This is an opt-in Windows desktop integration test, separate from the full toolbox smoke suite.
It generates its own animated window and captures that window only. It never enables a microphone.
It requires an unlocked interactive desktop, the recording engine, and FFmpeg/FFprobe.
CI can build this project; native capture assertions require a desktop test host.

Run the executable with `--capture`, an output folder, and an FFmpeg `bin` folder.
Without `--capture` the runner performs only validation checks; it does not record a screen.
`--audio` additionally plays a generated short tone and tests system loopback. A muted/zero-volume
output endpoint is reported as an explicit coverage skip; the test never changes user audio settings.
`--supplement` runs only startup cancellation, 60 FPS, and optional audio after option validation.
`--interruption` runs only read-only volume API consistency plus a simulated audio-monitor failure,
checks that its finalized video remains decodable and its error is preserved, and records one short
subsequent clip to verify resource release. It never disconnects a real device or captures audio.
Each run uses a new timestamped output directory, writes `results.json`, and exits nonzero on failure.
Video checks use actual decoded frames and MP4 metadata rather than only engine callbacks.

Coverage: generated window capture, actual changing video, dimensions/FPS, manual/automatic bitrate,
pause/resume duration, output non-overwrite, pre-cancelled start, invalid options, and silent recording.
Microphone, multi-monitor/DPI combinations, first-time permissions, protected content, long recordings,
and every GPU/driver combination are not covered by this fixture.
