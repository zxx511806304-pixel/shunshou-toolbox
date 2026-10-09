using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class ClipboardPinTests
{
    public static Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "clipboard-pins");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "pins.json");

        // 1) A missing file loads as an empty list.
        var store = ClipboardPinStore.Load(path);
        CompressionTests.Check(store.Pins.Count == 0, "缺失的置顶文件应加载为空列表");

        // 2) Added pins persist and reload newest-first.
        CompressionTests.Check(store.Add("第一段文字", path), "新增置顶应保存成功");
        CompressionTests.Check(store.Add("第二段文字", path), "再次新增置顶应保存成功");
        var reloaded = ClipboardPinStore.Load(path);
        CompressionTests.Check(reloaded.Pins.Count == 2, "重新加载应读到两条置顶");
        CompressionTests.Check(reloaded.Pins[0] == "第二段文字" && reloaded.Pins[1] == "第一段文字", "置顶顺序应保持最新在前");

        // 3) Re-adding existing text dedupes and promotes it.
        CompressionTests.Check(store.Add("第一段文字", path), "重复置顶应保存成功");
        reloaded = ClipboardPinStore.Load(path);
        CompressionTests.Check(reloaded.Pins.Count == 2, "重复置顶不应产生重复条目");
        CompressionTests.Check(reloaded.Pins[0] == "第一段文字", "重复置顶应提升到最前");

        // 4) Removal persists; removing unknown text still succeeds.
        CompressionTests.Check(store.Remove("第一段文字", path), "删除置顶应保存成功");
        reloaded = ClipboardPinStore.Load(path);
        CompressionTests.Check(reloaded.Pins.Count == 1 && reloaded.Pins[0] == "第二段文字", "删除后应只剩一条置顶");
        CompressionTests.Check(store.Remove("不存在的文字", path), "删除不存在的置顶不应失败");

        // 5) A damaged file is tolerated and loads as empty.
        File.WriteAllText(path, "{ 这不是合法的 JSON …");
        reloaded = ClipboardPinStore.Load(path);
        CompressionTests.Check(reloaded.Pins.Count == 0, "损坏的置顶文件应加载为空列表");

        // 6) Version mismatch loads empty; blank entries are skipped.
        File.WriteAllText(path, "{\"version\":2,\"pins\":[\"abc\"]}");
        CompressionTests.Check(ClipboardPinStore.Load(path).Pins.Count == 0, "版本不匹配应加载为空列表");
        File.WriteAllText(path, "{\"version\":1,\"pins\":[\"\", \"  \", \"保留\"]}");
        reloaded = ClipboardPinStore.Load(path);
        CompressionTests.Check(reloaded.Pins.Count == 1 && reloaded.Pins[0] == "保留", "空白置顶条目应被忽略");

        // 7) Blank text is rejected and not persisted.
        CompressionTests.Check(!store.Add("   ", path), "空白内容不应允许置顶");

        Console.WriteLine("PASS clipboard pins: persistence");
        return Task.CompletedTask;
    }
}
