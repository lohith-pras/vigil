using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Vigil;

/// <summary>
/// CI only: `Vigil.exe --memtest out.txt` runs the real app and logs memory at fixed points:
/// idle in the tray, with the panel open, and after it's hidden again. Then quits.
/// </summary>
static class MemTest
{
    public static void Schedule(Tray tray, Application app, string file)
    {
        var log = new StringBuilder();
        int sec = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            sec++;
            switch (sec)
            {
                case 10: Mark(log, "idle (tray only, 10 s)"); tray.ShowPanelForTest(); break;
                case 16: Mark(log, "panel open (6 s)"); tray.HidePanelForTest(); break;
                case 30: Mark(log, "after hide (14 s)"); break;
                case 31:
                    timer.Stop();
                    File.WriteAllText(file, log.ToString());
                    app.Shutdown();
                    break;
            }
        };
        timer.Start();
    }

    static void Mark(StringBuilder log, string label)
    {
        using var p = Process.GetCurrentProcess();
        log.AppendLine($"{label,-24} working set {p.WorkingSet64 >> 20,4} MB · private bytes {p.PrivateMemorySize64 >> 20,4} MB · managed heap {GC.GetTotalMemory(false) >> 20,3} MB");
    }
}
