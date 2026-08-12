using System.IO.Compression;
using System.Text.Json;

namespace ZBS.Skins;

/// <summary>
/// Пакет скина: модель + картинки. Понимает два формата:
/// .wsz — классика Winamp 2: zip с BMP-листами, модель строится по ClassicSkinSpec;
/// .zbs — наш: zip с manifest.json + layout.json + листами (BMP/PNG), самоописываемый.
/// Конвертер .wsz → .zbs = та же модель, сериализованная в layout.json + те же листы.
/// Скины — чужие архивы из интернета: лимиты на распаковку и валидация модели обязательны.
/// </summary>
public sealed class SkinPackage
{
    // Разумные потолки: классические скины — сотни КБ; всё сильно больше — мусор или бомба.
    private const int MaxEntries = 300;
    private const long MaxEntryBytes = 8 * 1024 * 1024;
    private const long MaxTotalBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".bmp", ".png", ".txt", ".json" };

    public SkinModel Model { get; private init; } = new();

    /// <summary>Пакет-тема: только палитра, без элементов — перекрашивает основной UI.</summary>
    public bool IsTheme => Model.Elements.Count == 0 && Model.Manifest.Theme is { Count: > 0 };

    /// <summary>Картинки пакета: lowercase-имя файла → байты (декодирует рендерер).</summary>
    public IReadOnlyDictionary<string, byte[]> Images { get; private init; } =
        new Dictionary<string, byte[]>();

    public static SkinPackage Load(string path)
    {
        var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? manifestJson = null;
        string? layoutJson = null;
        long total = 0;
        var entries = 0;

        using (var zip = ZipFile.OpenRead(path))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // папка
                var full = entry.FullName.Replace('\\', '/');
                if (full.Contains("__MACOSX/") || entry.Name.StartsWith("._")) continue; // мак-мусор
                if (!AllowedExtensions.Contains(Path.GetExtension(entry.Name))) continue;
                if (++entries > MaxEntries)
                    throw new InvalidDataException("skin archive: too many entries");
                if (entry.Length > MaxEntryBytes || (total += entry.Length) > MaxTotalBytes)
                    throw new InvalidDataException("skin archive: too large");

                var name = entry.Name.ToLowerInvariant(); // скины часто упакованы с подпапкой — плющим
                var depth = full.Count(c => c == '/');

                using var s = entry.Open();
                using var ms = new MemoryStream();
                CopyBounded(s, ms, MaxEntryBytes);
                var bytes = ms.ToArray();
                switch (name)
                {
                    case "manifest.json": manifestJson ??= System.Text.Encoding.UTF8.GetString(bytes); break;
                    case "layout.json": layoutJson ??= System.Text.Encoding.UTF8.GetString(bytes); break;
                    default:
                        // Дубликаты имён: выигрывает самый неглубокий (семантика Winamp),
                        // иначе бэкап-подпапки old/alt подменяли бы спрайты.
                        if (!depths.TryGetValue(name, out var prev) || depth < prev)
                        {
                            images[name] = bytes;
                            depths[name] = depth;
                        }
                        break;
                }
            }
        }

        SkinModel model;
        if (layoutJson is not null)
        {
            model = new SkinModel
            {
                Manifest = ParseManifest(manifestJson),
                Elements = JsonSerializer.Deserialize<List<SkinElement>>(layoutJson, SkinModel.Json) ?? new(),
            };
        }
        else
        {
            // .wsz (или .zbs без layout — считаем классикой, но манифест, если был, сохраняем)
            model = ClassicSkinSpec.Build(
                Path.GetFileNameWithoutExtension(path),
                new HashSet<string>(images.Keys, StringComparer.OrdinalIgnoreCase));
            if (manifestJson is not null)
                model.Manifest = ParseManifest(manifestJson);
        }

        Validate(model);
        return new SkinPackage { Model = model, Images = images };
    }

    private static void CopyBounded(Stream from, Stream to, long limit)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int got;
        while ((got = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += got;
            if (copied > limit)
                throw new InvalidDataException("skin archive: entry inflates beyond limit"); // zip-бомба
            to.Write(buffer, 0, got);
        }
    }

    private static SkinManifest ParseManifest(string? json)
    {
        if (json is null) return new SkinManifest();
        try
        {
            return JsonSerializer.Deserialize<SkinManifest>(json, SkinModel.Json) ?? new SkinManifest();
        }
        catch (JsonException)
        {
            return new SkinManifest();
        }
    }

    /// <summary>
    /// layout.json — пользовательский ввод: короткие массивы координат и нулевые размеры
    /// роняли бы рендер (IndexOutOfRange/DivideByZero в Render убивает приложение).
    /// Невалидные элементы просто выбрасываем.
    /// </summary>
    private static void Validate(SkinModel model)
    {
        if (model.Manifest.Size is not { Length: >= 2 } size || size[0] <= 0 || size[1] <= 0)
            model.Manifest.Size = new[] { 275, 116 };

        static bool Pair(int[]? a) => a is null or { Length: >= 2 };
        static bool Quad(int[]? a) => a is null or { Length: >= 4 };

        model.Elements.RemoveAll(el =>
        {
            if (!Pair(el.Src) || !Pair(el.SrcPressed) || !Pair(el.SrcOn) ||
                !Pair(el.SrcOnPressed) || !Pair(el.SrcBg) || !Pair(el.ThumbPressed) ||
                !Quad(el.Thumb))
                return true;
            return el.Type switch
            {
                SkinElementType.Background or SkinElementType.Button or SkinElementType.Toggle
                    or SkinElementType.SliderH or SkinElementType.Indicator => el.W <= 0 || el.H <= 0,
                SkinElementType.Digits => el.DigitW <= 0 || el.DigitH <= 0 || el.DigitsX is not { Length: > 0 },
                SkinElementType.Marquee => el.CharW <= 0 || el.CharH <= 0 || el.W <= 0,
                _ => false,
            };
        });
    }

    /// <summary>Конвертер: сохранить пакет как .zbs. Пишем во временный файл и подменяем атомарно —
    /// сбой на середине не должен уничтожить существующий скин.</summary>
    public void SaveAsZbs(string outPath)
    {
        var tmp = outPath + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                void PutText(string name, string text)
                {
                    var entry = zip.CreateEntry(name);
                    using var w = new StreamWriter(entry.Open());
                    w.Write(text);
                }

                PutText("manifest.json", JsonSerializer.Serialize(Model.Manifest, SkinModel.Json));
                PutText("layout.json", JsonSerializer.Serialize(Model.Elements, SkinModel.Json));
                foreach (var (name, bytes) in Images)
                {
                    var entry = zip.CreateEntry(name);
                    using var s = entry.Open();
                    s.Write(bytes);
                }
            }
            File.Move(tmp, outPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
