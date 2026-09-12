using Microsoft.UI.Xaml;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class RecoveryWorkspace
{
    internal async Task VerifyFixtureAsync(string input, string output)
    {
        Directory.CreateDirectory(output);
        string image = Path.Combine(output, "mode-selection.img");
        await File.WriteAllBytesAsync(image, new byte[512]);
        SelectSource(image);
        foreach (int mode in new[] { 2, 3, 0 })
        {
            SeedResult();
            ModeBox.SelectedIndex = mode;
            await Task.Delay(50);
            if (Source != image || SourceLabel.Text != image || _all.Count != 0 || _scanSource != null)
                throw new InvalidOperationException("Recovery disk-mode switch lost the selected image or retained stale results.");
        }
        foreach (int mode in new[] { 4, 1 })
        {
            SelectSource(image);
            await Task.Delay(50);
            SeedResult();
            ModeBox.SelectedIndex = mode;
            await Task.Delay(50);
            if (_sourceFile != null || _all.Count != 0 || _scanSource != null)
                throw new InvalidOperationException("Recovery incompatible mode retained an image or stale results.");
        }
        ModeBox.SelectedIndex = 4;
        await Task.Delay(50);
        _sourceFile = input;
        SourceLabel.Text = input;
        OutputFolder.Text = output;
        Scan_Click(this, new RoutedEventArgs());
        await WaitForOperationAsync();
        if (_all.Count != 2) throw new InvalidOperationException("Recovery backup scan did not list both generated files.");
        TypeFilter.Text = "png";
        for (int i = 0; i < 100 && _rows.Count != 1; i++) await Task.Delay(20);
        if (_rows.Count != 1 || _rows[0].Extension != "png") throw new InvalidOperationException("Recovery extension filter failed.");
        NameFilter.Text = "恢复";
        await Task.Delay(50);
        if (_rows.Count != 1) throw new InvalidOperationException("Recovery Chinese filename filter failed.");
        Results.SelectedIndex = 0;
        for (int i = 0; i < 100 && PreviewImage.Source == null; i++) await Task.Delay(50);
        if (PreviewImage.Source == null || !CopyButton.IsEnabled || !OcrButton.IsEnabled)
            throw new InvalidOperationException("Recovery image preview or selected recovery action failed.");
        Copy_Click(this, new RoutedEventArgs());
        await WaitForOperationAsync();
        string expected = Path.Combine(output, _rows[0].Name);
        if (!File.Exists(expected) || !File.Exists(_rows[0].StoredPath)) throw new InvalidOperationException("Recovery copy did not preserve source and output.");

        void SeedResult()
        {
            _all = [new("previous.txt", image, "previous.txt", 512, "verification")];
            _scanSource = image;
            ApplyFilter();
        }
    }

    private async Task WaitForOperationAsync()
    {
        for (int i = 0; i < 300 && _operation != null; i++) await Task.Delay(50);
        if (_operation != null || Notice.IsOpen && Notice.Severity == Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning)
            throw new InvalidOperationException("Recovery UI task did not complete: " + Notice.Message);
    }
}
