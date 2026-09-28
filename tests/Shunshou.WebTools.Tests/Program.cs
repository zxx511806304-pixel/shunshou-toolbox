using System.Diagnostics;
using System.Text.Json;
using Shunshou.Core;
using SkiaSharp;

string repo = Path.GetFullPath(args[0]);
string output = Path.Combine(repo, "artifacts", "web-tools-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(output);
string tools = Path.Combine(repo, "dist", "ShunshouToolbox-1.0.2-win-x64", "app", "tools");
string ffmpeg = Path.Combine(tools, "ffmpeg", "bin");
var checks = new List<object>();
var progress = new Progress<ToolProgress>(p => { if (p.Percent >= 94) Console.WriteLine(p.Message); });
async Task<string> Run(string exe, params string[] values)
{
    var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (string value in values) start.ArgumentList.Add(value);
    using var process = Process.Start(start)!;
    var text = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { try { process.Kill(true); } catch { } throw; }
    if (process.ExitCode != 0) throw new InvalidOperationException((await errors)[..Math.Min((await errors).Length, 1200)]);
    return await text;
}
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
try
{
    if (args.Contains("--online"))
    {
        var service = new VideoDownloadService(Path.Combine(tools, "video-download"), ffmpeg);
        foreach (var url in new[] { "https://www.bilibili.com/video/BV1bK411W797", "https://www.bilibili.com/video/BV13x41117TL" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                var file = await service.DownloadAsync(url, output, 360, progress, timeout.Token);
                string metadata = await Run(Path.Combine(ffmpeg,"ffprobe.exe"),"-v","error","-show_entries","stream=codec_type,codec_name,pix_fmt,width,height","-of","json",file);
                using var json = JsonDocument.Parse(metadata);
                Require(json.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_name").GetString() == "h264"), "Final video must be H264");
                string frame = Path.Combine(output, "online-" + checks.Count + ".png");
                await Run(Path.Combine(ffmpeg,"ffmpeg.exe"),"-v","error","-ss","5","-i",file,"-frames:v","1","-update","1",frame);
                using var bitmap = SKBitmap.Decode(frame);
                var colors = new HashSet<uint>(); double light = 0; int count = 0;
                for(int y=0;y<bitmap.Height;y+=5) for(int x=0;x<bitmap.Width;x+=5) { var c=bitmap.GetPixel(x,y); colors.Add((uint)c); light += (c.Red+c.Green+c.Blue)/3d; count++; }
                Require(colors.Count > 100 && light/count > 4, "Online video frame is empty or black");
                checks.Add(new { Url=url, Passed=true, Bytes=new FileInfo(file).Length, Streams=json.RootElement.Clone(), UniqueColors=colors.Count, MeanLuma=light/count });
                // Online media is temporary QA input; retain metrics, not copies for redistribution.
                File.Delete(frame); File.Delete(file);
            }
            catch(Exception ex) { checks.Add(new { Url=url, Passed=false, Error=ex.Message }); }
        }
    }
    else
    {
        string source = Path.Combine(output, "hevc.mkv");
        await Run(Path.Combine(ffmpeg,"ffmpeg.exe"),"-v","error","-f","lavfi","-i","testsrc2=size=320x184:rate=15:duration=2","-c:v","libkvazaar","-pix_fmt","yuv420p",source);
        string compatible = await VideoDownloadService.EnsureCompatibleVideoAsync(source,ffmpeg,Path.Combine(ffmpeg,"ffprobe.exe"),progress,CancellationToken.None);
        string metadata=await Run(Path.Combine(ffmpeg,"ffprobe.exe"),"-v","error","-show_entries","stream=codec_name,pix_fmt","-of","json",compatible);
        Require(metadata.Contains("h264") && metadata.Contains("yuv420p"),"HEVC not converted to Windows compatible H264");
        checks.Add(new { Check="HEVC actual decode and H264 conversion", Passed=true });
        string audio=Path.Combine(output,"audio.m4a");
        await Run(Path.Combine(ffmpeg,"ffmpeg.exe"),"-v","error","-f","lavfi","-i","sine=frequency=500:duration=1","-c:a","aac",audio);
        bool rejected=false; try { await VideoDownloadService.EnsureCompatibleVideoAsync(audio,ffmpeg,Path.Combine(ffmpeg,"ffprobe.exe"),null,CancellationToken.None); } catch(InvalidDataException) { rejected=true; }
        Require(rejected,"Audio-only input must not be published as successful video");
        Require(VideoDownloadService.ValidateUrl("分享给你 https://v.douyin.com/abc123/ 复制打开") == "https://v.douyin.com/abc123/","Share text URL extraction");
        checks.Add(new { Check="Audio-only rejection and mobile share text",Passed=true });
        for(int i=0;i<30;i++)
        {
            using var bitmap=new SKBitmap(640,360); using var canvas=new SKCanvas(bitmap);
            canvas.Clear(new SKColor((byte)(20+i*3),(byte)(35+i*2),(byte)(100+i*2)));
            using var paint=new SKPaint{Color=SKColors.Yellow,IsAntialias=true}; canvas.DrawCircle(100+i*9,160,45,paint);
            using var font=new SKFont(SKTypeface.FromFamilyName("Arial"),24); paint.Color=SKColors.White;
            canvas.DrawText("SHUNSHOU",465,42,font,paint); canvas.DrawText("CAPTION "+i,250,318,font,paint);
            using var image=SKImage.FromBitmap(bitmap); using var data=image.Encode(SKEncodedImageFormat.Png,100);
            using var stream=File.Create(Path.Combine(output,$"frame-{i:00}.png")); data.SaveTo(stream);
        }
        string fixture=Path.Combine(output,"watermark.mp4");
        await Run(Path.Combine(ffmpeg,"ffmpeg.exe"),"-v","error","-framerate","10","-i",Path.Combine(output,"frame-%02d.png"),"-c:v","libopenh264","-b:v","2000000","-pix_fmt","yuv420p",fixture);
        var edit=new VideoWatermarkService(ffmpeg); var inspection=await edit.InspectAsync(fixture,Path.Combine(output,"preview.png"),CancellationToken.None);
        var detector=new WatermarkDetectionService(ffmpeg,Path.Combine(tools,"ocr"));
        var candidates=await detector.DetectAsync(fixture,inspection,progress,CancellationToken.None);
        Require(candidates.Any(c=>c.Region.X>430 && c.Region.Y<60),"Persistent top-right watermark was not detected");
        Require(!candidates.Any(c=>c.Region.X>150 && c.Region.X<430 && c.Region.Y>260),"Changing bottom subtitles were misclassified");
        var blankSamples=Enumerable.Range(0,5).Select(_=>(IReadOnlyList<TextLayoutBlock>)Array.Empty<TextLayoutBlock>()).ToArray();
        Require(WatermarkDetectionService.FindTextCandidates(blankSamples,640,360).Count==0,"Blank input must have no candidates");
        string preview=await edit.PreviewAsync(fixture,output,VideoWatermarkMode.Blur,candidates[0].Region,null,CancellationToken.None);
        Require(new FileInfo(preview).Length>1000,"Detected region cannot flow through preview");
        checks.Add(new { Check="Actual multi-frame OCR detection, subtitle exclusion, blank detection and preview",Passed=true,Candidates=candidates });
    }
    await File.WriteAllTextAsync(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {Passed=checks.All(c=>!JsonSerializer.Serialize(c).Contains("\"Passed\":false")),Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("RESULTS: "+Path.Combine(output,"results.json"));
    return checks.Any(c=>JsonSerializer.Serialize(c).Contains("\"Passed\":false")) ? 1 : 0;
}
catch(Exception ex)
{
    await File.WriteAllTextAsync(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {Passed=false,Checks=checks,Error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(ex); return 1;
}
