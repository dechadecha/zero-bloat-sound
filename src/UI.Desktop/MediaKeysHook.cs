using System.Runtime.InteropServices;

namespace ZBS.UI.Desktop;

/// <summary>
/// Глобальные мультимедиа-клавиши через низкоуровневый хук Windows.
/// Хук живёт на СВОЁМ потоке с циклом сообщений: если бы он висел на UI-потоке,
/// любой затык интерфейса тормозил бы клавиатуру всей системы, и Windows молча
/// снимала бы хук по таймауту. Клавиши не проглатываются. На не-Windows — no-op.
/// </summary>
public sealed class MediaKeysHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int WmQuit = 0x0012;
    private const int VkMediaNext = 0xB0;
    private const int VkMediaPrev = 0xB1;
    private const int VkMediaStop = 0xB2;
    private const int VkMediaPlayPause = 0xB3;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Value;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out NativeMessage lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly HookProc _proc; // держим в поле — иначе GC соберёт делегат под нативным хуком
    private Thread? _thread;
    private IntPtr _hook;
    private uint _threadId;

    /// <summary>События приходят из потока хука — подписчик маршалит сам.</summary>
    public event Action? PlayPausePressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;
    public event Action? StopPressed;

    public MediaKeysHook() => _proc = Callback;

    public void Install()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return;
        _thread = new Thread(HookThreadLoop) { IsBackground = true, Name = "ZBS.MediaKeys" };
        _thread.Start();
    }

    private void HookThreadLoop()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero) return;
        while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
        {
            // Хуку нужен только живой цикл сообщений — сами сообщения не интересны.
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WmKeydown || wParam == WmSyskeydown))
        {
            var vk = Marshal.ReadInt32(lParam); // первый int KBDLLHOOKSTRUCT — vkCode
            try
            {
                switch (vk)
                {
                    case VkMediaPlayPause: PlayPausePressed?.Invoke(); break;
                    case VkMediaNext: NextPressed?.Invoke(); break;
                    case VkMediaPrev: PreviousPressed?.Invoke(); break;
                    case VkMediaStop: StopPressed?.Invoke(); break;
                }
            }
            catch { /* исключение не должно уйти в нативный хук */ }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_thread is null) return;
        if (_threadId != 0)
            PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(500);
        _thread = null;
    }
}
