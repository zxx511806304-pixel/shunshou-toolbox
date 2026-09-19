# Recording window and shortcut checks

Run this Windows-only target from a normal interactive desktop:

```powershell
./.tools/dotnet/dotnet.exe run --project tests/Shunshou.NativeRecordingTests
```

The test briefly opens the app's region-selection overlays and posts messages only to windows owned by the test process. It does not move the pointer, type into other applications, capture the desktop, or save screenshots. Ctrl+Alt+F9 and Ctrl+Alt+F10 must be available at test start; all registrations are released on completion.

Checks cover physical negative coordinates, per-monitor DPI awareness, forward/reverse/clamped dragging, minimum dimensions, concurrent selection, Escape/right-click/focus cancellation, both shortcut callbacks and conflicts, partial registration rollback, re-registration after disposal, and GDI resource cleanup. Results are written to `native-results.json` next to the test executable.

These checks do not claim mixed-DPI visual QA or verify the encoder, audio devices, or the main WinUI interface.
