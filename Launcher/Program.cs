using System.Diagnostics;

namespace DeepSharp.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var binDirectory = AppContext.BaseDirectory;
        var application = Path.Combine(binDirectory, "DeepSharp.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = application,
            WorkingDirectory = binDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("ui");
        startInfo.ArgumentList.Add("--shortcut");
        Process.Start(startInfo);
    }
}
