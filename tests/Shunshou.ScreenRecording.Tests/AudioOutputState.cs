using System.Runtime.InteropServices;

namespace Shunshou.ScreenRecording.Tests;

internal sealed record AudioOutputState(string DeviceId, float Volume, bool Muted)
{
    internal static AudioOutputState Read()
    {
        var enumerator = (IDeviceEnumerator)(object)new DeviceEnumerator();
        IDevice? device = null;
        object? volumeObject = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
            Marshal.ThrowExceptionForHR(device.GetId(out var id));
            var iid = typeof(IEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out volumeObject));
            var volume = (IEndpointVolume)volumeObject;
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var scalar));
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            return new(id, scalar, muted);
        }
        finally
        {
            if (volumeObject is not null) Marshal.ReleaseComObject(volumeObject);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float value, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float value, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float value);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float value);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float value, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float value, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float value);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float value);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool value, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool value);
    }
}
