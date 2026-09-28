using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Shunshou.App;
public sealed partial class MainWindow
{
    private async Task<int> VerifyWebToolsAsync(string output, string? sitesFile)
    {
        Directory.CreateDirectory(output);
        try
        {
            NavigateToTool("text", "链接提取文字");
            var fixture=await WebPdfTools.VerifyTextFixtureAsync(output);
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark,ElementTheme.Light})
            {
                RootLayout.RequestedTheme=theme; await Task.Delay(650); await SaveScreenshot(Path.Combine(output,"text-"+theme+".png"));
            }
            var sites=new List<object>();
            if(sitesFile is not null)
            {
                using var input=JsonDocument.Parse(await File.ReadAllTextAsync(sitesFile));
                foreach(var site in input.RootElement.EnumerateArray())
                {
                    var result=await WebPdfTools.ProbeLivePageAsync(site.GetProperty("url").GetString()!,site.GetProperty("video").GetBoolean());
                    sites.Add(result); await File.WriteAllTextAsync(Path.Combine(output,"live-sites.json"),JsonSerializer.Serialize(sites,new JsonSerializerOptions{WriteIndented=true}));
                }
            }
            await File.WriteAllTextAsync(Path.Combine(output,"ui-results.json"),JsonSerializer.Serialize(new{Passed=true,Fixture=fixture,OnlineResults=sites,Note="Public page checks store counts and status only, not chapter bodies or browser cookies."},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"ui-results.json"),JsonSerializer.Serialize(new{Passed=false,Error=ex.ToString()}));return 1;
        }
    }
}
