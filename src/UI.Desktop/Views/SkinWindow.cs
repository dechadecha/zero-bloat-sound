using Avalonia.Controls;
using ZBS.Skins;
using ZBS.UI.Desktop.Controls;
using ZBS.UI.Desktop.ViewModels;

namespace ZBS.UI.Desktop.Views;

/// <summary>Окно классического скина: без рамки, размер — из скина (×2), тащится за любое место.</summary>
public sealed class SkinWindow : Window
{
    public SkinWindow(SkinPackage package, MainViewModel vm)
    {
        Title = package.Model.Manifest.Name is { Length: > 0 } n ? n : "Zero-Bloat Sound";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = true;
        Content = new SkinControl(package, vm,
            closeRequested: Close,
            minimizeRequested: () => WindowState = WindowState.Minimized);
    }
}
