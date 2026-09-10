using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Shunshou.App;

/// <summary>Explicit developer-only checks in the real WinUI process; never changes the system clipboard.</summary>
internal static class OcrWorkspaceVerification
{
    public static async Task<IReadOnlyList<string>> RunAsync(OcrWorkspace workspace, string fixtureImage,
        string outputDirectory, Func<string, Task>? capture = null)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        fixtureImage = Path.GetFullPath(fixtureImage);
        string replacement = Path.Combine(outputDirectory, "second-image.png");
        File.Copy(fixtureImage, replacement, true);
        byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(fixtureImage));
        var checks = new List<string>();

        Require(await workspace.SetInputAsync(fixtureImage), "OCR rejected a valid fixture image.");
        Require(workspace.InputPath == fixtureImage && workspace.PreviewImage is { PixelWidth: > 0, PixelHeight: > 0 } preview
            && preview.PixelWidth <= 640 && preview.PixelHeight <= 640, "OCR did not show a bounded source preview.");
        string text = await workspace.RunRecognitionAsync();
        Require(!string.IsNullOrWhiteSpace(text) && workspace.ResultText == text && !workspace.HasUnsavedEdits,
            "Real OCR result did not populate the editable result pane.");
        Require(text.Contains("SHUNSHOU", StringComparison.OrdinalIgnoreCase) && text.Contains("12345")
            && text.Contains("文字") && text.Contains("\r\n"),
            "The OCR editor lost recognized fixture characters or line boundaries.");
        Require(Directory.GetFiles(outputDirectory, "*.txt").Length == 0, "OCR automatically saved an unrequested TXT file.");
        checks.Add("Real offline OCR populates the editable pane with a bounded original-image preview and no automatic TXT output");
        if (capture is not null) await CaptureStableAsync(workspace, Path.Combine(outputDirectory, "ocr-result.png"), capture);

        var editor = (TextBox)workspace.FindName("ResultEditor");
        editor.Focus(FocusState.Programmatic);
        Require(!workspace.CanHandlePasteShortcut(), "OCR image paste would intercept ordinary text-editor Ctrl+V.");
        ((Button)workspace.FindName("PasteButton")).Focus(FocusState.Programmatic);
        Require(workspace.CanHandlePasteShortcut(), "OCR image paste shortcut is not available outside text editors.");
        workspace.Visibility = Visibility.Collapsed;
        Require(!workspace.CanHandlePasteShortcut(), "Hidden OCR workspace would consume Ctrl+V.");
        workspace.Visibility = Visibility.Visible;
        checks.Add("Ctrl+V respects focused editable text and only applies to the visible OCR workspace");

        const string editInput = "手工修正的文字\nHello, clipboard! 123\r第三行\r\n\r\n结尾";
        const string edited = "手工修正的文字\r\nHello, clipboard! 123\r\n第三行\r\n\r\n结尾";
        workspace.EditTextForVerification(editInput);
        Require(workspace.HasUnsavedEdits, "Editing recognized text was not tracked.");
        Require(workspace.ResultText == edited, "Editor normalization lost manual characters, blank lines or paragraph boundaries.");
        Require(await workspace.CreateTextPackage().GetView().GetTextAsync() == edited,
            "Copy package included content other than the current edited body.");
        workspace.ConfirmDiscardOverride = () => Task.FromResult(false);
        Require(!await workspace.SetInputAsync(replacement) && workspace.InputPath == fixtureImage && workspace.ResultText == edited,
            "Declining discard lost the selected image or manual text.");
        string saved = Path.Combine(outputDirectory, "edited-result.txt");
        await workspace.SaveTextToPathAsync(saved);
        Require(await File.ReadAllTextAsync(saved) == edited && !workspace.HasUnsavedEdits,
            "Explicit TXT save lost edits or retained a dirty state.");
        workspace.EditTextForVerification("");
        Require(workspace.HasUnsavedEdits, "Deleting all recognized text was not tracked as an edit.");
        workspace.ConfirmDiscardOverride = () => Task.FromResult(true);
        Require(await workspace.SetInputAsync(replacement) && workspace.ResultText.Length == 0 && !workspace.HasUnsavedEdits,
            "Confirmed image replacement retained old text.");
        checks.Add("Manual editing, text-only copy payload, declined discard, explicit UTF-8 save and confirmed replacement preserve user intent");

        string broken = Path.Combine(outputDirectory, "broken.png");
        await File.WriteAllTextAsync(broken, "not an image");
        bool rejected = false;
        try { await workspace.SetInputAsync(broken); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && workspace.InputPath == replacement && !workspace.IsBusy,
            "Invalid image replaced the valid selection or left the workspace busy.");
        var empty = new DataPackage();
        empty.SetText("this is ordinary text");
        rejected = false;
        try { await workspace.PasteDataAsync(empty.GetView()); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && workspace.InputPath == replacement && !workspace.IsBusy,
            "Text-only clipboard content was accepted as an image.");
        checks.Add("Corrupt image and text-only clipboard failures retain the prior input and release the busy state");

        var file = await StorageFile.GetFileFromPathAsync(fixtureImage);
        var files = new DataPackage();
        files.SetStorageItems([file]);
        Require(await workspace.PasteDataAsync(files.GetView()) && workspace.InputPath == fixtureImage,
            "Copied image-file input did not select the actual file.");
        var bitmap = new DataPackage();
        bitmap.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Require(await workspace.PasteDataAsync(bitmap.GetView()), "Bitmap clipboard data was rejected.");
        string clipboardPath = workspace.InputPath!;
        Require(clipboardPath != fixtureImage && File.Exists(clipboardPath) && workspace.PreviewImage is not null,
            "Bitmap paste did not create its own previewable image.");
        Require(await workspace.SetInputAsync(fixtureImage) && File.Exists(clipboardPath)
            && await workspace.SetInputAsync(clipboardPath),
            "Changing selection made a pasted batch image unavailable.");
        await workspace.SetInputAsync(fixtureImage);
        Require(SHA256.HashData(await File.ReadAllBytesAsync(fixtureImage)).SequenceEqual(originalHash),
            "OCR workspace changed the source image.");
        checks.Add("Bitmap and copied-file DataPackage paths work without altering the system clipboard; pasted batch images remain selectable until session cleanup");

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayed = new OcrWorkspace((_, _) => completion.Task);
        Require(await delayed.SetInputAsync(fixtureImage), "Cancellation test could not set its input.");
        using var cancellation = new CancellationTokenSource();
        Task<string> pending = delayed.RunRecognitionAsync(cancellation.Token);
        Require(delayed.IsBusy && !await delayed.SetInputAsync(replacement), "Recognition allowed its input to change in flight.");
        cancellation.Cancel();
        completion.TrySetResult("stale result must not appear");
        bool cancelled = false;
        try { await pending; }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && delayed.ResultText.Length == 0 && !delayed.IsBusy,
            "A cancelled recognizer's late result was displayed.");
        Require(await delayed.PasteDataAsync(bitmap.GetView()), "Disposal test could not create a pasted image.");
        string finalTemporary = delayed.InputPath!;
        delayed.Dispose();
        Require(!File.Exists(finalTemporary) && File.Exists(fixtureImage), "Disposal cleanup missed its own image or deleted a source file.");
        checks.Add("Recognition locks input; cancelled late completions cannot overwrite text; disposal removes owned clipboard images without touching originals");

        workspace.ConfirmDiscardOverride = null;
        await workspace.RunRecognitionAsync();
        if (capture is not null) await CaptureStableAsync(workspace, Path.Combine(outputDirectory, "ocr-ready.png"), capture);
        return checks;
    }

    private static async Task CaptureStableAsync(OcrWorkspace workspace, string path, Func<string, Task> capture)
    {
        await Task.Delay(200);
        workspace.UpdateLayout();
        Require(workspace.FindName("WorkspaceNotice") is InfoBar { IsOpen: false, Visibility: Visibility.Collapsed }
            && workspace.FindName("CopyButton") is Button { IsEnabled: true }
            && workspace.FindName("SaveButton") is Button { IsEnabled: true },
            "Completed OCR retained a status banner or disabled export controls.");
        await capture(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
