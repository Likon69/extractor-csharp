using System.Diagnostics;
using System.Windows;
using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Debug.WriteLine($"MaNGOS Extractor v1.0 — WotLK build {WowConstants.TargetBuild}");
        Debug.WriteLine($"Format: map=v1.5 | vmap=VMAPt07 | mmap=t06");
    }
}
