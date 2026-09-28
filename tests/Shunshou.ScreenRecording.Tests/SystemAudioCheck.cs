using System.Runtime.InteropServices;
using Shunshou.Core;

namespace Shunshou.ScreenRecording.Tests;

internal static class SystemAudioCheck
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    internal static async Task RunAsync(ScreenRecordingService recorder, ScreenRecordingOptions options,
        IntPtr window, string ffmpeg, string root, List<object> results, List<string> uncovered)
    {
        var outputs = recorder.GetAudioDevices(input: false);
        if (outputs.Count == 0) { uncovered.Add("System loopback audio could not run because no output endpoint was available."); return; }
        var endpoint = AudioOutputState.Read();
        if (endpoint.Muted || endpoint.Volume <= 0)
        {
            uncovered.Add("Generated-tone content verification was skipped because the Windows default output endpoint is muted or volume is zero; user audio settings were not changed.");
            results.Add(new { Name = "generated-system-tone", Skipped = true, Reason = "System output muted or volume zero", Endpoint = endpoint,
                AudioContentVerified = false, MicrophoneEnabled = false });
            Console.WriteLine("SKIP generated-tone content: Windows output is muted or volume zero.");
            return;
        }
        Program.Check(!options.CaptureMicrophone, "microphone remains disabled for generated tone test");
        var tonePath = Path.Combine(root, "generated-tone.wav");
        WriteTone(tonePath);
        await recorder.StartWindowAsync(options with { AudioBitrateKbps = 128 }, window);
        string recorded;
        try
        {
            // An explicit --audio invocation opts into this short generated tone; never a microphone.
            Program.Check(PlaySound(tonePath, IntPtr.Zero, 0x00020000 | 0x0001 | 0x0002), "generated test tone playback starts");
            await Task.Delay(3200);
            recorded = await recorder.StopAsync();
        }
        finally { PlaySound(null, IntPtr.Zero, 0); }
        var metadata = await VideoInspection.InspectAsync(ffmpeg, recorded, Path.Combine(root, "system-audio"));
        Program.Check(metadata.AudioStreams == 1, "system sound option creates an audio stream");
        var pcmPath = Path.Combine(root, "system-audio.pcm");
        await VideoInspection.RunAsync(Path.Combine(ffmpeg, "ffmpeg.exe"), "-v", "error", "-i", recorded,
            "-map", "0:a:0", "-ac", "1", "-ar", "16000", "-f", "s16le", "-y", pcmPath);
        var pcm = await File.ReadAllBytesAsync(pcmPath);
        var count = pcm.Length / 2;
        double power = 0, sin = 0, cos = 0;
        for (int i = 0; i < count; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 2) / 32768d;
            power += sample * sample;
            sin += sample * Math.Sin(2 * Math.PI * 1000 * i / 16000);
            cos += sample * Math.Cos(2 * Math.PI * 1000 * i / 16000);
        }
        var rms = Math.Sqrt(power / Math.Max(1, count));
        var toneAmplitude = 2 * Math.Sqrt(sin * sin + cos * cos) / Math.Max(1, count);
        await File.WriteAllTextAsync(Path.Combine(root, "audio-content-metrics.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Rms = rms, Tone1000HzAmplitude = toneAmplitude, Samples = count, Endpoint = endpoint }));
        Program.Check(rms > 0.001 && toneAmplitude > 0.001, "decoded loopback PCM contains the generated 1 kHz tone");
        results.Add(new { Name = "generated-system-tone", Video = metadata, Rms = rms, Tone1000HzAmplitude = toneAmplitude,
            MicrophoneEnabled = false, RequestedAudioKbps = 128, Output = recorded });
    }

    private static void WriteTone(string path)
    {
        const int rate = 48000, seconds = 4;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var bytes = rate * seconds * 2;
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes);
        for (var i = 0; i < rate * seconds; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 32767 * 0.12));
    }
}
