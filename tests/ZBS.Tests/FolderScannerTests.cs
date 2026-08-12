using ZBS.Core.Playlist;
using Xunit;

namespace ZBS.Tests;

public sealed class FolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zbs-test-" + Guid.NewGuid().ToString("N"));

    public FolderScannerTests() => Directory.CreateDirectory(_root);

    private string Touch(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Array.Empty<byte>());
        return full;
    }

    [Fact]
    public void Scan_collects_supported_files_recursively_in_disk_order()
    {
        Touch("b.mp3");
        Touch("a.flac");
        Touch("notes.txt");
        Touch(Path.Combine("sub1", "c.ogg"));
        Touch(Path.Combine("sub1", "skip.jpg"));
        Touch(Path.Combine("sub2", "d.wav"));

        var tracks = FolderScanner.Scan(_root);

        Assert.Equal(
            new[] { "a.flac", "b.mp3", "c.ogg", "d.wav" },
            tracks.Select(t => Path.GetFileName(t.FilePath)).ToArray());
    }

    [Fact]
    public void Scan_of_empty_folder_returns_empty_list()
    {
        Assert.Empty(FolderScanner.Scan(_root));
    }

    [Fact]
    public void CollectFromPaths_merges_files_and_folders_into_one_playlist()
    {
        var f1 = Touch("single.mp3");
        Touch(Path.Combine("dir", "one.flac"));
        Touch(Path.Combine("dir", "two.ogg"));
        var skipped = Touch("readme.txt");

        var tracks = FolderScanner.CollectFromPaths(new[] { f1, Path.Combine(_root, "dir"), skipped });

        Assert.Equal(
            new[] { "single.mp3", "one.flac", "two.ogg" },
            tracks.Select(t => Path.GetFileName(t.FilePath)).ToArray());
    }

    [Fact]
    public void IsSupported_is_case_insensitive()
    {
        Assert.True(FolderScanner.IsSupported("X:\\music\\track.MP3"));
        Assert.True(FolderScanner.IsSupported("X:\\music\\track.FlAc"));
        Assert.False(FolderScanner.IsSupported("X:\\music\\cover.png"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
