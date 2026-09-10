namespace Shunshou.Core;

public sealed record InputSelectionSnapshot(
    IReadOnlyList<string> Paths,
    string? SelectedPath,
    bool Restored = false,
    int KeptInPreviousTool = 0,
    string? Message = null);

/// <summary>
/// In-memory input drafts only: this class never copies, moves or deletes a file.
/// Image conversion and OCR share one batch; other tools retain separate drafts.
/// </summary>
public sealed class InputSelectionWorkspace
{
    private readonly Dictionary<InputTool, InputSelectionSnapshot> _drafts = [];
    private InputTool? _currentTool;

    public void SaveCurrent(IEnumerable<string> paths, string? selectedPath = null)
    {
        if (_currentTool is not { } tool) return;
        string[] snapshot = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? selected = snapshot.FirstOrDefault(path => StringComparer.OrdinalIgnoreCase.Equals(path, selectedPath));
        _drafts[DraftKey(tool)] = new(snapshot, selected);
    }

    /// <summary>
    /// Call before replacing the previous tool's displayed inputs. A previously visited tool restores
    /// its own draft (including an intentionally cleared draft). On first visit only compatible inputs
    /// transfer. A single-input tool never silently chooses one of multiple compatible paths.
    /// </summary>
    public InputSelectionSnapshot SwitchTo(InputTool tool, IEnumerable<string> previousPaths, string? selectedPath = null)
    {
        string[] previous = previousPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SaveCurrent(previous, selectedPath);
        _currentTool = tool;
        var key = DraftKey(tool);
        if (_drafts.TryGetValue(key, out var existing)) return existing with { Restored = true };

        string[] compatible = InputSelectionPolicy.CompatiblePaths(tool, previous).ToArray();
        string? selected = compatible.FirstOrDefault(path => StringComparer.OrdinalIgnoreCase.Equals(path, selectedPath));
        int retained = previous.Length - compatible.Length;
        string? message = null;
        if (!InputSelectionPolicy.AllowsMultiple(tool) && compatible.Length > 1)
        {
            if (selected != null)
            {
                retained = previous.Length - 1;
                compatible = [selected];
                message = $"当前工具使用选中的文件，其余 {retained} 项保留在原工具中。";
            }
            else
            {
                retained = previous.Length;
                compatible = [];
                message = "这项工具一次处理一个输入；原工具中的文件已保留，请选择要处理的文件。";
            }
        }
        else if (retained > 0)
            message = compatible.Length == 0 ? "原工具中的文件已保留，可切换回去继续处理。" : $"已带入 {compatible.Length} 项兼容输入，其余 {retained} 项保留在原工具中。";
        selected ??= compatible.FirstOrDefault();
        var created = new InputSelectionSnapshot(compatible, selected, KeptInPreviousTool: retained, Message: message);
        _drafts[key] = created with { KeptInPreviousTool = 0, Message = null };
        return created;
    }

    private static InputTool DraftKey(InputTool tool) => tool == InputTool.Ocr ? InputTool.ImageConvert : tool;
}
