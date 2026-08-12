using ZBS.Library;
using Xunit;

namespace ZBS.Tests;

public sealed class LibraryDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zbs-db-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;

    public LibraryDbTests() => _db = new LibraryDb(_dir);

    private static TrackRow Row(string path, string? title, string? artist, double? rg = null,
        double segStart = 0, double? segEnd = null, string? id = null) =>
        new(id ?? path, path, segStart, segEnd, title, artist, "Album", "Rock", 2020, 180, rg, 1);

    [Fact]
    public void Upsert_search_and_counters_work()
    {
        _db.UpsertMany(new[]
        {
            Row(@"D:\m\a.mp3", "Song A", "Artist One", -6.5),
            Row(@"D:\m\b.mp3", "Song B", "Artist Two"),
        });

        var found = _db.Search("One");
        Assert.Single(found);
        Assert.Equal("Song A", found[0].Title);
        Assert.Equal(-6.5, found[0].GainDb!.Value, precision: 3);

        _db.IncrementPlayCount(@"D:\m\a.mp3");
        _db.SetRating(@"D:\m\a.mp3", 5);
        var again = _db.Search("Song A")[0];
        Assert.Equal(1, again.PlayCount);
        Assert.Equal(5, again.Rating);
    }

    [Fact]
    public void Search_is_case_insensitive_for_cyrillic()
    {
        _db.UpsertMany(new[] { Row(@"D:\m\v.mp3", "Кони привередливые", "Высоцкий") });

        Assert.Single(_db.Search("высоцкий"));
        Assert.Single(_db.Search("ВЫСОЦКИЙ"));
        Assert.Single(_db.Search("кони"));
    }

    [Fact]
    public void Search_escapes_like_wildcards()
    {
        _db.UpsertMany(new[]
        {
            Row(@"D:\m\p.mp3", "100% хит", "X"),
            Row(@"D:\m\q.mp3", "Другое", "Y"),
        });

        Assert.Single(_db.Search("100%")); // «%» ищется буквально, а не «всё подряд»
        Assert.Empty(_db.Search("1_0%"));  // «_» тоже не джокер
    }

    [Fact]
    public void RemoveFolder_does_not_touch_sibling_with_common_prefix()
    {
        // Платформенные пути: RemoveFolder сверяет префикс с родным разделителем ОС.
        var music = Path.Combine(Path.GetTempPath(), "Music");
        var music2 = Path.Combine(Path.GetTempPath(), "Music2");
        _db.AddFolder(music);
        _db.AddFolder(music2);
        _db.UpsertMany(new[]
        {
            Row(Path.Combine(music, "a.mp3"), "In Music", "X"),
            Row(Path.Combine(music2, "b.mp3"), "In Music2", "Y"),
        });

        _db.RemoveFolder(music);

        var left = _db.Search("");
        Assert.Single(left);
        Assert.Equal("In Music2", left[0].Title);
    }

    [Fact]
    public void Cue_segments_live_as_separate_rows_with_own_counters()
    {
        var file = @"D:\m\album.flac";
        _db.UpsertMany(new[]
        {
            Row(file, "Intro", "Band", rg: -3, segStart: 0, segEnd: 120, id: file + "#0"),
            Row(file, "Main", "Band", rg: -3, segStart: 120, segEnd: 300, id: file + "#120"),
        });

        Assert.Equal(2, _db.TrackCount());
        Assert.Single(_db.Search("Intro"));
        Assert.Equal(-3, _db.GetGainDbByFile(file)!.Value, precision: 3);

        _db.IncrementPlayCount(file + "#0");
        var intro = _db.Search("Intro")[0];
        var main = _db.Search("Main")[0];
        Assert.Equal(1, intro.PlayCount);
        Assert.Equal(0, main.PlayCount); // сосед не пострадал
    }

    [Fact]
    public void RemoveMissing_deletes_gone_ids_only()
    {
        _db.UpsertMany(new[]
        {
            Row(@"D:\m\keep.mp3", "Keep", "X"),
            Row(@"D:\m\gone.mp3", "Gone", "X"),
        });

        _db.RemoveMissing(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\m\keep.mp3" });

        Assert.Equal(1, _db.TrackCount());
        Assert.Equal("Keep", _db.Search("")[0].Title);
    }

    [Fact]
    public void GetAllMtimes_returns_known_stamps()
    {
        _db.UpsertMany(new[] { Row(@"D:\m\a.mp3", "A", "X") });
        var mtimes = _db.GetAllMtimes();
        Assert.Equal(1, mtimes[@"D:\m\a.mp3"]);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
