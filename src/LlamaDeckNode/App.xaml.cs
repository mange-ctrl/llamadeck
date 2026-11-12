using System.IO;
using System.Windows;

namespace LlamaDeckNode;

public static class AppPaths
{
    public static bool Portable { get; private set; }
    public static string DataDir { get; private set; } = "";

    public static void Init(bool portable)
    {
        Portable = portable;
        var exeDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(exeDir, "data");
        if (portable && TryCreate(candidate))
        {
            DataDir = candidate;
            return;
        }

        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LlamaDeckNode");
        Directory.CreateDirectory(DataDir);
    }

    private static bool TryCreate(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var portable = e.Args.Contains("--portable") || File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
        AppPaths.Init(portable);

        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
        base.OnStartup(e);
    }
}
