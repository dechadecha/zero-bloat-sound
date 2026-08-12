using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZBS.Core.Settings;

/// <summary>
/// JSON-хранилище настроек. Портативный режим: файл portable.txt рядом с exe —
/// настройки живут в папке приложения; иначе — в профиле пользователя.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string? overrideDirectory = null)
    {
        var dir = overrideDirectory ?? ResolveDirectory();
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public string FilePath => _path;

    public static string ResolveDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.txt")))
            return Path.Combine(exeDir, "data");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZeroBloatSound");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                loaded.LastfmSessionKey = Unprotect(loaded.LastfmSessionKey); // расшифровать креды
                loaded.LastfmApiSecret = Unprotect(loaded.LastfmApiSecret);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битые настройки не должны ронять плеер — стартуем с дефолтом.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var plainKey = settings.LastfmSessionKey;
        var plainSecret = settings.LastfmApiSecret;
        try
        {
            // Оба Last.fm-credential на диск — зашифрованными DPAPI (session key бессрочен, secret — тоже секрет).
            settings.LastfmSessionKey = Protect(plainKey);
            settings.LastfmApiSecret = Protect(plainSecret);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Недоступный диск — не повод падать при выходе.
        }
        finally
        {
            settings.LastfmSessionKey = plainKey; // в памяти держим расшифрованным
            settings.LastfmApiSecret = plainSecret;
        }
    }

    // Last.fm креды на Windows шифруем DPAPI (привязка к юзеру ОС): копия settings.json
    // (бэкап/синхра/инфостилер) не даёт готовый доступ. ОГРАНИЧЕНИЕ: на Linux/macOS кросс-платформенного
    // DPAPI нет, креды ложатся как есть — там защита при копировании файла НЕ действует (нужен
    // keychain/libsecret, отдельная задача). Приложение Windows-first, фича по умолчанию выключена.
    private const string Marker = "dpapi:";

    private static string Protect(string s)
    {
        if (string.IsNullOrEmpty(s) || !OperatingSystem.IsWindows()) return s;
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
            return Marker + Convert.ToBase64String(bytes);
        }
        // Сбой DPAPI (роуминг/групповые политики): НЕ пишем секрет открытым текстом — лучше сбросить,
        // пользователь перелогинится/перевведёт ключ. Так контроль не обходится молча.
        catch (CryptographicException) { return ""; }
    }

    private static string Unprotect(string s)
    {
        if (!s.StartsWith(Marker, StringComparison.Ordinal)) return s;
        if (!OperatingSystem.IsWindows()) return ""; // зашифровано на другой машине — не восстановить, перелогинится
        try
        {
            var bytes = Convert.FromBase64String(s[Marker.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }
}
