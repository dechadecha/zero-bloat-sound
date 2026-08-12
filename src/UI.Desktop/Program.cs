using Avalonia;

namespace ZBS.UI.Desktop;

internal static class Program
{
    /// <summary>Держатель single-instance на время жизни процесса (доступен App-у для подписки на аргументы).</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = new SingleInstance();

        // Пути нормализуем ДО пересылки: у первого экземпляра другой рабочий каталог,
        // и «zbs.exe track.mp3» из консоли иначе молча потеряется.
        var payload = args.Length > 0
            ? args.Select(SafeFullPath).ToArray()
            : new[] { SingleInstance.ActivateCommand };

        if (!instance.IsPrimary)
        {
            if (SingleInstance.ForwardArgs(payload))
                return 0;
            // Первый экземпляр завис или умирает — не оставляем пользователя ни с чем:
            // запускаемся как обычный плеер (single-instance в этом запуске не гарантируется).
        }

        Instance = instance;
        instance.StartListening(); // для не-primary тихо не делает ничего

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static string SafeFullPath(string arg)
    {
        try { return Path.GetFullPath(arg); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return arg;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
