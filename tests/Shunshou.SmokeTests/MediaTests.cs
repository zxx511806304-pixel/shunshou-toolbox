using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class MediaTests
{
    public static async Task RunPrecisionAsync(string root)
    {
        root = Path.Combine(Path.GetFullPath(root), "media-precision");
        Directory.CreateDirectory(root);
        var engines = FindEngines();
        var service = new MediaService(engines);
        var output = Path.Combine(root, "output");
        var source24 = Path.Combine(root, "integer-24bit.wav");
        await Command(engines, "ffmpeg.exe", ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "anoisesrc=color=pink:sample_rate=48000:duration=2:seed=123", "-c:a", "pcm_s24le", source24]);
        var flac = await service.ConvertAsync(source24, output, "flac", null, null, default);
        var recovered = await service.ConvertAsync(flac, output, "wav", null, null, default);
        var pcmBefore = Path.Combine(root, "before.pcm");
        var pcmAfter = Path.Combine(root, "after.pcm");
        await Command(engines, "ffmpeg.exe", ["-v", "error", "-nostdin", "-y", "-i", source24, "-c:a", "pcm_s24le", "-f", "s24le", pcmBefore]);
        await Command(engines, "ffmpeg.exe", ["-v", "error", "-nostdin", "-y", "-i", recovered, "-c:a", "pcm_s24le", "-f", "s24le", pcmAfter]);
        CompressionTests.Check(CompressionTests.HashFile(pcmBefore) == CompressionTests.HashFile(pcmAfter), "24-bit random PCM is sample-exact through FLAC and WAV");
        var probe = await Command(engines, "ffprobe.exe", ["-v", "error", "-show_entries", "stream=bits_per_raw_sample", "-of", "json", flac]);
        using (var json = JsonDocument.Parse(probe))
            CompressionTests.Check(json.RootElement.GetProperty("streams")[0].GetProperty("bits_per_raw_sample").GetString() == "24", "FLAC retains actual 24-bit declaration");
        var floating = Path.Combine(root, "floating-32bit.wav");
        await Command(engines, "ffmpeg.exe", ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "pcm_f32le", floating]);
        await CompressionTests.Throws<NotSupportedException>(() => service.ConvertAsync(floating, output, "flac", null, null, default), "floating samples cannot silently quantize into FLAC");
        var source32 = Path.Combine(root, "integer-32bit.wav");
        await Command(engines, "ffmpeg.exe", ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "anoisesrc=sample_rate=48000:duration=2:seed=456", "-c:a", "pcm_s32le", source32]);
        // The pinned FFmpeg encoder currently writes at most 24-bit FLAC. The service must reject the reduced precision.
        await CompressionTests.Throws<InvalidDataException>(() => service.ConvertAsync(source32, output, "flac", null, null, default), "32-bit integer precision is not silently reduced to 24 bits");
        Console.WriteLine("PASS: 24-bit FLAC sample-exact roundtrip; float FLAC rejected; 32-bit-to-24-bit quantization detected and unpublished.");
    }

    public static async Task RunAsync(string root)
    {
        root = Path.Combine(Path.GetFullPath(root), "media");
        Directory.CreateDirectory(root);
        var engines = FindEngines();
        var service = new MediaService(engines);
        var wav = Path.Combine(root, "合成音频 $(literal) & '测试'.wav");
        await Command(engines, "ffmpeg.exe", ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
            "sine=frequency=880:sample_rate=48000:duration=24", "-ac", "2", "-c:a", "pcm_s16le", wav]);
        var originalHash = CompressionTests.HashFile(wav);
        var output = Path.Combine(root, "output");
        var mp3 = await service.ConvertAsync(wav, output, "mp3", 200_000, null, default);
        CompressionTests.Check(new FileInfo(mp3).Length <= 195_000, "MP3 actual target size includes 2.5% margin");
        await VerifyProbe(engines, mp3, 24, "mp3", false);
        var secondMp3 = await service.ConvertAsync(wav, output, "mp3", null, null, default);
        CompressionTests.Check(mp3 != secondMp3 && File.Exists(mp3), "repeated conversions never overwrite existing output");
        CompressionTests.Check(CompressionTests.HashFile(wav) == originalHash, "MP3 source remains byte-identical");
        var m4a = await service.ConvertAsync(wav, output, "m4a", 200_000, null, default);
        CompressionTests.Check(new FileInfo(m4a).Length <= 195_000, "M4A actual target size checked");
        await VerifyProbe(engines, m4a, 24, "aac", false);

        var flac = await service.ConvertAsync(wav, output, "flac", null, null, default);
        await VerifyProbe(engines, flac, 24, "flac", false);
        var roundtripWav = await service.ConvertAsync(flac, output, "wav", null, null, default);
        var originalPcm = Path.Combine(root, "original.pcm");
        var resultPcm = Path.Combine(root, "roundtrip.pcm");
        await Command(engines, "ffmpeg.exe", ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", wav, "-f", "s16le", "-c:a", "pcm_s16le", originalPcm]);
        await Command(engines, "ffmpeg.exe", ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", roundtripWav, "-f", "s16le", "-c:a", "pcm_s16le", resultPcm]);
        CompressionTests.Check(CompressionTests.HashFile(originalPcm) == CompressionTests.HashFile(resultPcm), "16-bit WAV to FLAC to WAV retains exact audio samples");
        Console.WriteLine($"PASS: WAV → MP3 {new FileInfo(wav).Length / 1000d:F1} KB → {new FileInfo(mp3).Length / 1000d:F1} KB; M4A target, full 24 seconds, sample-exact WAV/FLAC roundtrip and no overwrite.");

        var video = Path.Combine(root, "合成画面.mp4");
        await Command(engines, "ffmpeg.exe", ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
            "testsrc2=size=640x360:rate=24:duration=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12",
            "-c:v", "libopenh264", "-rc_mode", "bitrate", "-b:v", "3000000", "-c:a", "aac", "-b:a", "128000", "-pix_fmt", "yuv420p", video]);
        var videoHash = CompressionTests.HashFile(video);
        var compressedVideo = await service.ConvertAsync(video, output, "mp4", 900_000, null, default);
        CompressionTests.Check(new FileInfo(compressedVideo).Length <= 877_500, "MP4 actual target includes upload margin");
        await VerifyProbe(engines, compressedVideo, 12, "h264", true);
        var extractedAudio = await service.ConvertAsync(video, output, "mp3", null, null, default);
        await VerifyProbe(engines, extractedAudio, 12, "mp3", false);
        CompressionTests.Check(CompressionTests.HashFile(video) == videoHash, "video source remains byte-identical");
        Console.WriteLine($"PASS: MP4 {new FileInfo(video).Length / 1000d:F1} KB → {new FileInfo(compressedVideo).Length / 1000d:F1} KB; full 12 seconds, 640×360, H.264/AAC and extracted MP3 verified.");

        await CompressionTests.Throws<ArgumentException>(() => service.ConvertAsync(wav, output, "flac", 100_000, null, default), "lossless format cannot promise arbitrary target size");
        await CompressionTests.Throws<InvalidOperationException>(() => service.ConvertAsync(wav, output, "mp3", 1024, null, default), "impossible audio target explicitly rejected");
        await CompressionTests.Throws<InvalidOperationException>(() => service.ConvertAsync(video, output, "mp4", 1024, null, default), "impossible video target explicitly rejected");
        var bad = Path.Combine(root, "broken.mp4");
        File.WriteAllText(bad, "This is deliberately not a media file.");
        await CompressionTests.Throws<InvalidDataException>(() => service.ConvertAsync(bad, output, "mp3", null, null, default), "corrupt input");
        var cancellationOutput = Path.Combine(root, "cancelled-output");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await CompressionTests.Throws<OperationCanceledException>(() => service.ConvertAsync(wav, cancellationOutput, "mp3", null, null, cancelled.Token), "pre-cancelled media task");
        using var during = new CancellationTokenSource();
        var progress = new CancellationProgress(during);
        await CompressionTests.Throws<OperationCanceledException>(() => service.ConvertAsync(video, cancellationOutput, "mp4", null, progress, during.Token), "cancellation while FFmpeg converts");
        CompressionTests.Check(!Directory.Exists(cancellationOutput) || !Directory.EnumerateFiles(cancellationOutput).Any(), "cancelled operation publishes no partial output");
        CompressionTests.Check(CompressionTests.HashFile(video) == videoHash && CompressionTests.HashFile(wav) == originalHash, "failed and cancelled tasks preserve originals");
        Console.WriteLine("PASS: offline FFmpeg media target errors, corrupted input, argument-safe special-character paths, cancellation and no partial publication.");
    }

    private sealed class CancellationProgress(CancellationTokenSource cancellation) : IProgress<ToolProgress>
    {
        private bool _scheduled;
        public void Report(ToolProgress value)
        {
            if (_scheduled || value.Percent < 5) return;
            _scheduled = true;
            cancellation.CancelAfter(50);
        }
    }

    private static async Task VerifyProbe(string engines, string path, double duration, string codec, bool video)
    {
        var output = await Command(engines, "ffprobe.exe", ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,width,height", "-of", "json", path]);
        using var parsed = JsonDocument.Parse(output);
        var seconds = double.Parse(parsed.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        CompressionTests.Check(Math.Abs(seconds - duration) < 0.5, "output retains full media duration");
        var streams = parsed.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        CompressionTests.Check(streams.Any(stream => stream.GetProperty("codec_name").GetString() == codec), "output is really encoded in requested codec");
        if (video)
        {
            var picture = streams.First(stream => stream.GetProperty("codec_type").GetString() == "video");
            CompressionTests.Check(picture.GetProperty("width").GetInt32() == 640 && picture.GetProperty("height").GetInt32() == 360, "target size does not reduce video resolution");
            CompressionTests.Check(streams.Any(stream => stream.GetProperty("codec_name").GetString() == "aac"), "MP4 keeps the audio track");
        }
    }

    private static string FindEngines()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            var candidate = Path.Combine(parent.FullName, "runtime", "ffmpeg", "bin");
            if (File.Exists(Path.Combine(candidate, "ffmpeg.exe"))) return candidate;
        }
        throw new FileNotFoundException("Smoke tests need runtime/ffmpeg/bin/ffmpeg.exe.");
    }

    private static async Task<string> Command(string directory, string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(directory, executable))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new Exception("Cannot launch media fixture tool.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new Exception("Media fixture/verification command failed: " + stderr);
        return stdout;
    }
}
