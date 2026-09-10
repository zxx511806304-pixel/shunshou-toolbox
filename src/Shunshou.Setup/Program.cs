namespace Shunshou.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length >= 2 && args[0] == "--verify-setup")
                return SetupVerification.RunAsync(args).GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--verify-setup-ui")
                return SetupVerification.RunUi(args[1]);
            if (args.Length == 2 && args[0] == "--screenshot")
            {
                using var form = new SetupForm(false);
                form.Shown += async (_, _) =>
                {
                    await Task.Delay(350);
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    var full = Path.GetFullPath(args[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    bitmap.Save(full, System.Drawing.Imaging.ImageFormat.Png);
                    form.Close();
                };
                Application.Run(form);
                return 0;
            }
            Application.Run(new SetupForm());
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Length > 0) { Console.Error.WriteLine(ex); return 1; }
            MessageBox.Show(ex.Message, "顺手工具箱", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
