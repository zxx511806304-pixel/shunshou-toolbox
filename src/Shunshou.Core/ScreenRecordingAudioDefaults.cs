using System.Runtime.InteropServices;

namespace Shunshou.Core;

/// <summary>Reads the Windows default endpoint. No audio client is activated and no samples are captured.</summary>
internal static class ScreenRecordingAudioDefaults
{
    internal static (bool IsMuted, float Volume) GetOutputAudioLevel(string? deviceId)
    {
        IDeviceEnumerator? enumerator = null;
        IDevice? device = null;
        IEndpointVolume? volume = null;
        nint pointer = 0;
        try
        {
            enumerator = (IDeviceEnumerator)(object)new DeviceEnumerator();
            if (string.IsNullOrWhiteSpace(deviceId)) Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
            else Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out device));
            var iid = typeof(IEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out pointer));
            volume = (IEndpointVolume)Marshal.GetObjectForIUnknown(pointer);
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var scalar));
            return (muted, Math.Clamp(scalar, 0f, 1f));
        }
        finally
        {
            if (pointer != 0) Marshal.Release(pointer);
            if (volume is not null) Marshal.FinalReleaseComObject(volume);
            if (device is not null) Marshal.FinalReleaseComObject(device);
            if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator);
        }
    }

    internal static string GetDefaultDeviceId(bool input)
    {
        IDeviceEnumerator? enumerator = null;
        IDevice? device = null;
        try
        {
            enumerator = (IDeviceEnumerator)(object)new DeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(input ? 1 : 0, 0, out device));
            Marshal.ThrowExceptionForHR(device.GetId(out var id));
            return id;
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(input ? "Windows 尚未设置可用的默认麦克风，请在设备列表中明确选择。"
                : "Windows 尚未设置可用的默认声音输出设备，请在设备列表中明确选择。", ex);
        }
        finally
        {
            if (device is not null) Marshal.FinalReleaseComObject(device);
            if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out nint collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, nint activation, out nint result);
        [PreserveSig] int OpenPropertyStore(int access, out nint properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint callback);
        [PreserveSig] int UnregisterControlChangeNotify(nint callback);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
