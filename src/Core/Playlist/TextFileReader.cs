using System.Text;

namespace ZBS.Core.Playlist;

/// <summary>
/// Чтение текстовых файлов эпохи винампа: UTF-8, а если декодер дал «кракозябры» —
/// перечитываем в windows-1251 (легаси m3u/pls/cue с русских Windows).
/// </summary>
internal static class TextFileReader
{
    static TextFileReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string[] ReadAllLinesSmart(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Contains('�'))
            text = File.ReadAllText(path, Encoding.GetEncoding(1251));
        return text.Replace("\r\n", "\n").Split('\n');
    }
}
