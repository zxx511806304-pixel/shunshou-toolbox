using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace Shunshou.Core;

public sealed record NetCheckItem(string Name, bool Ok, string Detail);

public sealed record NetReport(IReadOnlyList<NetCheckItem> Items, DateTime Time);

/// <summary>Reads local network state only: adapters, ping, DNS, proxy and hosts. No traffic is sent beyond the checks themselves.</summary>
public static class NetworkDiagnosticsService
{
    private const int PingTimeoutMs = 1500;

    public static async Task<NetReport> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var items = new List<NetCheckItem>();

        progress?.Report("正在读取网卡与地址…");
        items.Add(CheckAdapters());
        ct.ThrowIfCancellationRequested();

        var gateway = FindDefaultGateway();
        progress?.Report("正在 Ping 默认网关…");
        items.Add(await PingAsync("默认网关", gateway, ct));
        ct.ThrowIfCancellationRequested();

        progress?.Report("正在 Ping 公共 DNS 223.5.5.5…");
        items.Add(await PingAsync("公共 DNS 223.5.5.5", "223.5.5.5", ct));
        ct.ThrowIfCancellationRequested();

        progress?.Report("正在解析 www.baidu.com…");
        items.Add(await ResolveAsync("www.baidu.com", ct));
        ct.ThrowIfCancellationRequested();

        progress?.Report("正在解析 www.microsoft.com…");
        items.Add(await ResolveAsync("www.microsoft.com", ct));
        ct.ThrowIfCancellationRequested();

        progress?.Report("正在读取系统代理设置…");
        items.Add(CheckProxy());

        progress?.Report("正在统计 hosts 文件条目…");
        items.Add(CheckHosts());

        return new NetReport(items, DateTime.Now);
    }

    private static NetCheckItem CheckAdapters()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToArray();
            if (active.Length == 0) return new("网卡与 IP", false, "没有找到已启用的网卡。");
            var lines = new List<string>();
            var hasGateway = false;
            foreach (var nic in active)
            {
                var props = nic.GetIPProperties();
                var v4 = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).ToArray();
                var gateways = props.GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address.ToString()).ToArray();
                if (gateways.Length > 0) hasGateway = true;
                string address = v4.Length > 0 ? string.Join("、", v4) : "无 IPv4 地址";
                string gateway = gateways.Length > 0 ? string.Join("、", gateways) : "无默认网关";
                lines.Add($"{nic.Name}：{address}（网关 {gateway}）");
            }
            return new("网卡与 IP", hasGateway, string.Join("\n", lines));
        }
        catch (Exception ex) when (ex is NetworkInformationException or IOException or NotSupportedException or PlatformNotSupportedException)
        {
            return new("网卡与 IP", false, "读取网卡信息失败：" + ex.Message);
        }
    }

    private static string? FindDefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(g => g.Address.ToString())
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is NetworkInformationException or IOException or NotSupportedException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static async Task<NetCheckItem> PingAsync(string name, string? address, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(address)) return new(name, false, "没有可用的默认网关。");
        using var ping = new Ping();
        try
        {
            using var registration = ct.Register(() => ping.SendAsyncCancel());
            var reply = await ping.SendPingAsync(address, PingTimeoutMs);
            return reply.Status == IPStatus.Success
                ? new(name, true, $"{address} · {reply.RoundtripTime} ms")
                : new(name, false, $"{address} · {reply.Status}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is PingException or SocketException or NetworkInformationException)
        {
            return new(name, false, $"{address} · {ex.Message}");
        }
    }

    private static async Task<NetCheckItem> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
            return addresses.Length > 0
                ? new($"DNS 解析 {host}", true, string.Join("、", addresses.Select(a => a.ToString())))
                : new($"DNS 解析 {host}", false, "没有返回 IPv4 地址。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new($"DNS 解析 {host}", false, ex.Message);
        }
    }

    private static NetCheckItem CheckProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return new("系统代理", true, "未读取到代理设置，按未开启处理。");
            bool enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;
            var server = key.GetValue("ProxyServer") as string ?? "";
            return enabled
                ? new("系统代理", true, $"已开启 · {server}")
                : new("系统代理", true, "未开启系统代理。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new("系统代理", false, "读取代理设置失败：" + ex.Message);
        }
    }

    private static NetCheckItem CheckHosts()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        try
        {
            if (!File.Exists(path)) return new("hosts 文件", false, "没有找到 hosts 文件。");
            int count = 0;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#')) count++;
            }
            return new("hosts 文件", true, $"共 {count} 条生效记录");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new("hosts 文件", false, "读取 hosts 失败：" + ex.Message);
        }
    }

    public static async Task<string> FlushDnsAsync()
    {
        try
        {
            var start = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 ipconfig。");
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) return string.IsNullOrWhiteSpace(output) ? "DNS 缓存已清理。" : output.Trim();
            return string.IsNullOrWhiteSpace(error) ? $"ipconfig 退出码 {process.ExitCode}" : error.Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            return "清理失败：" + ex.Message;
        }
    }

    public static Task RunNetworkResetAsync()
    {
        var start = new ProcessStartInfo("cmd.exe", "/c netsh winsock reset && netsh int ip reset && ipconfig /flushdns")
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        try { Process.Start(start); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        return Task.CompletedTask;
    }

    public static async Task SaveReportAsync(NetReport report, string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("顺手工具箱 · 网络诊断报告");
        builder.AppendLine($"生成时间：{report.Time:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        foreach (var item in report.Items)
        {
            builder.AppendLine($"{(item.Ok ? "✓" : "✗")} {item.Name}");
            builder.AppendLine($"    {item.Detail.Replace("\n", "\n    ")}");
        }
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8);
    }
}
