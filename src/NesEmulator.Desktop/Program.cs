namespace NesEmulator.Desktop;

internal static class Program
{
    /// <summary>应用程序入口。使用 MainForm 而不是设计器生成的默认窗体。</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
    }
}
