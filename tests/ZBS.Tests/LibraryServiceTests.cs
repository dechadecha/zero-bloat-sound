using ZBS.Core.Playlist;
using ZBS.Library;
using Xunit;

namespace ZBS.Tests;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zbs-svc-" + Guid.NewGuid().ToString("N"));

    private static TrackRow Row(string path, string? title, string? artist, double duration = 180) =>
        new(path, path, 0, null, title, artist, "Album", "Rock", 2020, duration, null, 1);

    private void Seed(params TrackRow[] rows)
    {
        using var db = new LibraryDb(_dir);
        db.UpsertMany(rows);
    }

    [Fact]
    public void FindDuplicates_groups_same_artist_title_duration()
    {
        Seed(
            Row(@"D:\m\a.mp3", "Песня", "Артист", 180),
            Row(@"D:\m\copy\a.mp3", "песня", "артист", 181), // регистр и ±2 сек — тот же трек
            Row(@"D:\m\other.mp3", "Другая", "Артист", 180));

        using var svc = new LibraryService(_dir);
        var dups = svc.FindDuplicates();

        Assert.Equal(2, dups.Count);
        Assert.All(dups, d => Assert.Equal("песня", d.Title!.ToLowerInvariant()));
    }

    [Fact]
    public void FindBroken_reports_missing_and_undecodable_files()
    {
        var real = Path.Combine(_dir, "real.mp3");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(real, new byte[] { 1 });
        Seed(
            Row(real, "Ok", "X"),
            Row(Path.Combine(_dir, "gone.mp3"), "Gone", "X"));

        using var svc = new LibraryService(_dir);
        var broken = svc.FindBroken(_ => true); // декодер «всё открывает» — ловим только пропавшие

        Assert.Single(broken);
        Assert.Equal("Gone", broken[0].Title);

        var allBroken = svc.FindBroken(_ => false); // декодер «ничего не открывает»
        Assert.Equal(2, allBroken.Count);
    }

    [Fact]
    public void Search_filters_by_artist_and_genre()
    {
        Seed(
            Row(@"D:\m\a.mp3", "One", "Alpha"),
            Row(@"D:\m\b.mp3", "Two", "Beta"));

        using var svc = new LibraryService(_dir);
        Assert.Single(svc.Search("", artist: "Alpha"));
        Assert.Equal(2, svc.Search("", genre: "Rock").Count);
        Assert.Empty(svc.Search("", genre: "Jazz"));
        Assert.Equal(new[] { "Alpha", "Beta" }, svc.GetArtists());
    }

    [Fact]
    public void Genre_normalization_unifies_case_and_drops_junk()
    {
        Assert.Equal("Blues", TagReader.NormalizeGenre("blues"));
        Assert.Equal("Blues", TagReader.NormalizeGenre("BLUES"));
        Assert.Equal("Blues", TagReader.NormalizeGenre("  Blues  "));
        Assert.Equal("Русский рок", TagReader.NormalizeGenre("русский  РОК"));
        // Разделители: дефис/пробел — один жанр.
        Assert.Equal("Hip hop", TagReader.NormalizeGenre("Hip hop"));
        Assert.Equal("Hip hop", TagReader.NormalizeGenre("Hip-hop"));
        Assert.Equal("Hip hop", TagReader.NormalizeGenre("HIP_HOP"));
        // Многозначный тег — берём первый.
        Assert.Equal("Hip hop", TagReader.NormalizeGenre("Hip-hop, rap"));
        Assert.Equal("Rock", TagReader.NormalizeGenre("Rock/Metal"));
        Assert.Equal("Pop", TagReader.NormalizeGenre("pop;dance"));
        // Склейки без разделителя НЕ трогаем (это тег-редактор, не нормализация).
        Assert.Equal("Ambientgenre", TagReader.NormalizeGenre("ambientgenre"));
        Assert.Null(TagReader.NormalizeGenre("255"));
        Assert.Null(TagReader.NormalizeGenre("(17)"));
        Assert.Null(TagReader.NormalizeGenre("_"));
        Assert.Null(TagReader.NormalizeGenre("  "));
    }

    [Fact]
    public void FixLegacyCyrillic_recovers_cp1251_mojibake()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // «Этот город» в cp1251, прочитанное как Latin-1, даёт эту крякозябру:
        var mojibake = System.Text.Encoding.GetEncoding("ISO-8859-1")
            .GetString(System.Text.Encoding.GetEncoding(1251).GetBytes("Этот город"));
        Assert.Equal("Этот город", TagReader.FixLegacyCyrillic(mojibake));
    }

    [Fact]
    public void FixLegacyCyrillic_recovers_mixed_ascii_and_cyrillic()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var l1 = System.Text.Encoding.GetEncoding("ISO-8859-1");
        var cp = System.Text.Encoding.GetEncoding(1251);
        // «(rap-game.ru) Беста» — кириллица в меньшинстве, но серией подряд:
        var mojibake = "(rap-game.ru) " + l1.GetString(cp.GetBytes("Беста"));
        Assert.Equal("(rap-game.ru) Беста", TagReader.FixLegacyCyrillic(mojibake));
    }

    [Fact]
    public void StripSiteJunk_removes_downloader_tags_keeps_real_parens()
    {
        Assert.Equal("juanes - la camisa negra", TagReader.StripSiteJunk("juanes - la camisa negra (zaycev.net)"));
        Assert.Equal("Бандэрос - Полосы", TagReader.StripSiteJunk("Бандэрос - Полосы  (audiopoisk.com)"));
        Assert.Equal("Баста", TagReader.StripSiteJunk("(rap-game.ru) Баста"));
        Assert.Null(TagReader.StripSiteJunk("http://wap.sasisa.ru")); // весь тег — мусор
        // Легитимные скобки не трогаем:
        Assert.Equal("Rise Up (Eurovision GR 2014)", TagReader.StripSiteJunk("Rise Up (Eurovision GR 2014)"));
        Assert.Equal("Kamennye cvety (pri uch. Elena Vaenga)", TagReader.StripSiteJunk("Kamennye cvety (pri uch. Elena Vaenga)"));
    }

    [Fact]
    public void CleanFileName_turns_underscores_and_strips_junk()
    {
        // Путь строим платформенно — на Linux/mac «D:\m\…» не путь, а часть имени файла.
        var path = Path.Combine(Path.GetTempPath(), "m", "Madcon_-_Freaky_like_me_-_(mp3poisk.net).mp3");
        Assert.Equal("Madcon - Freaky like me", TagReader.CleanFileName(path));
    }

    [Fact]
    public void FixLegacyCyrillic_leaves_valid_text_alone()
    {
        Assert.Equal("Написано верно", TagReader.FixLegacyCyrillic("Написано верно")); // уже кириллица
        Assert.Equal("Written in the Stars", TagReader.FixLegacyCyrillic("Written in the Stars")); // англ.
        Assert.Equal("Café résumé", TagReader.FixLegacyCyrillic("Café résumé")); // одиночные акценты — не мянглим
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
