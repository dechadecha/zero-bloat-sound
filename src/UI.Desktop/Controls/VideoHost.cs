using Avalonia.Controls;
using Avalonia.Platform;

namespace ZBS.UI.Desktop.Controls;

/// <summary>
/// Дочернее нативное окно для рендера libmpv (wid). Живёт в дереве ПОСТОЯННО
/// (сворачивается в 0×0, а не прячется IsVisible) — mpv не переживает смену HWND.
/// </summary>
public sealed class VideoHost : NativeControlHost
{
    /// <summary>HWND создан — можно отдавать mpv. Стреляет при первом попадании в дерево.</summary>
    public event Action<IntPtr>? SurfaceCreated;

    public IntPtr SurfaceHandle { get; private set; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        SurfaceHandle = handle.Handle;
        SurfaceCreated?.Invoke(handle.Handle);
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        SurfaceHandle = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }
}
