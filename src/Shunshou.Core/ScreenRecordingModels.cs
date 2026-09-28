namespace Shunshou.Core;

/// <summary>Physical pixels. A region is relative to the selected display; display bounds are virtual-desktop coordinates.</summary>
public readonly record struct ScreenRecordingBounds(int X, int Y, int Width, int Height);

public sealed record ScreenRecordingDisplay(string DeviceName, string DisplayName, ScreenRecordingBounds Bounds, bool IsPrimary)
{
    public override string ToString() => $"{DisplayName} · {Bounds.Width} × {Bounds.Height}" + (IsPrimary ? " · 主屏幕" : "");
}

public sealed record ScreenRecordingAudioDevice(string Id, string Name, bool IsInput)
{
    public override string ToString() => Name;
}

public sealed record ScreenRecordingOptions
{
    public required string DisplayDeviceName { get; init; }
    public required string OutputDirectory { get; init; }
    public ScreenRecordingBounds? Region { get; init; }
    public bool CaptureSystemAudio { get; init; } = true;
    public bool CaptureMicrophone { get; init; }
    public string? AudioOutputDeviceId { get; init; }
    public string? AudioInputDeviceId { get; init; }
    public bool ShowCursor { get; init; } = true;
    public int FrameRate { get; init; } = 30;
    /// <summary>Null chooses a rate based on the resulting frame size and frame rate; units are megabits per second.</summary>
    public double? VideoBitrateMbps { get; init; }
    /// <summary>Null keeps original dimensions; otherwise downscale proportionally, never upscale.</summary>
    public int? MaxOutputHeight { get; init; }
    public int AudioBitrateKbps { get; init; } = 192;
    public bool PreferHardwareEncoding { get; init; } = true;
}

public enum ScreenRecordingState { Idle, Starting, Recording, Paused, Stopping, Completed, Failed }

public sealed record ScreenRecordingStatus(ScreenRecordingState State, TimeSpan Elapsed, string? OutputPath, string? Error);

/// <summary>The validated physical crop and exact dimensions/encoder settings submitted to the recording engine.</summary>
public sealed record ScreenRecordingPlan(ScreenRecordingDisplay Display, ScreenRecordingBounds Region,
    int OutputWidth, int OutputHeight, int FrameRate, int VideoBitrate, int AudioBitrateKbps);
