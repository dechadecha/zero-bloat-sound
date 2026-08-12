using System.IO;
using ZBS.Library;
using Xunit;

namespace ZBS.Tests;

public class MetadataResolverTests
{
    private static FileTags Empty => new(null, null, null, null, 0, 0, null);
    private static FileTags WithArtist(string? artist, string? title = null) =>
        new(title, artist, null, null, 0, 0, null);

    // Корень/пути строим через Path.Combine — тесты проходят и на Linux/macOS CI.
    private static string Root => Path.Combine("D:", "Music");
    private static string P(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

    [Theory]
    [InlineData("juanes - la camisa negra", "juanes", "la camisa negra")]
    [InlineData("Rammstein - Чернобыль", "Rammstein", "Чернобыль")]
    [InlineData("Nik CHernikov-Frendzona", "Nik CHernikov", "Frendzona")] // голый дефис, слева пробел
    [InlineData("Би-2 - Серебро", "Би-2", "Серебро")]                     // пробельный сепаратор в приоритете
    public void SplitArtistTitle_splits_valid_pairs(string name, string artist, string title)
    {
        var (a, t) = MetadataResolver.SplitArtistTitle(name);
        Assert.Equal(artist, a);
        Assert.Equal(title, t);
    }

    [Theory]
    [InlineData("Rock-n-Roll")]      // голый дефис, но слева нет пробела — не рвём слово
    [InlineData("well-known")]
    [InlineData("Я - Легенда")]      // левая часть в 1 символ — не артист
    [InlineData("simple title")]     // нет разделителя
    public void SplitArtistTitle_rejects_non_pairs(string name)
    {
        var (a, _) = MetadataResolver.SplitArtistTitle(name);
        Assert.Null(a);
    }

    [Fact]
    public void Resolve_empty_tags_derives_artist_and_title_from_filename()
    {
        var (artist, title, _) = MetadataResolver.Resolve(
            P("Разное", "Rammstein - Чернобыль.mp3"), Root, Empty);
        Assert.Equal("Rammstein", artist);
        Assert.Equal("Чернобыль", title);
    }

    [Fact]
    public void Resolve_empty_tags_without_separator_uses_folder_artist()
    {
        var (artist, title, album) = MetadataResolver.Resolve(
            P("Ария", "Химера", "1100.mp3"), Root, Empty);
        Assert.Equal("Ария", artist);       // верхняя папка
        Assert.Equal("Химера", album);      // папка альбома
        Assert.Equal("1100", title);        // имя файла без разбора
    }

    [Fact]
    public void Resolve_strips_duplicated_artist_prefix_from_title()
    {
        var (_, title, _) = MetadataResolver.Resolve(
            P("Dino MC47", "trk.mp3"), Root, WithArtist("Dino MC47", "Dino MC47 - Никто не забыт"));
        Assert.Equal("Никто не забыт", title);
    }

    [Fact]
    public void Resolve_keeps_self_titled_track()
    {
        var (_, title, _) = MetadataResolver.Resolve(
            P("Billy Milligan", "x.mp3"), Root, WithArtist("Billy Milligan", "Billy Milligan"));
        Assert.Equal("Billy Milligan", title); // title == artist: не режем в пустоту
    }

    [Fact]
    public void Resolve_placeholder_album_folder_ignored()
    {
        var (_, _, album) = MetadataResolver.Resolve(
            P("Ария", "Без альбома", "1100.mp3"), Root, Empty);
        Assert.Null(album);
    }

    [Fact]
    public void Resolve_prefers_tag_artist_over_folder()
    {
        var (artist, _, _) = MetadataResolver.Resolve(
            P("Папка", "x.mp3"), Root, WithArtist("Настоящий Артист", "Песня"));
        Assert.Equal("Настоящий Артист", artist);
    }

    [Theory]
    [InlineData("Баста, GUF", "Баста")]
    [InlineData("Баста feat Бандерос", "Баста")]
    [InlineData("Ария, Udo Dirkschneider", "Ария")]
    [InlineData("5sta Family & DJ Pankratov", "5sta Family")]
    [InlineData("Гарри Топор, Тони Раут", "Гарри Топор")]
    [InlineData("Король и Шут", "Король и Шут")]      // « и » НЕ режем — это имя группы
    [InlineData("Время и Стекло", "Время и Стекло")]
    [InlineData("Баста", "Баста")]
    public void PrimaryArtist_takes_first_of_collab(string tag, string primary)
    {
        Assert.Equal(primary, MetadataResolver.PrimaryArtist(tag));
    }

    [Fact]
    public void ArtistKey_merges_spelling_variants()
    {
        Assert.Equal(MetadataResolver.ArtistKey("БАНД'ЭРОС"), MetadataResolver.ArtistKey("БандЭрос"));
        Assert.NotEqual(MetadataResolver.ArtistKey("Банда"), MetadataResolver.ArtistKey("БандЭрос"));
    }
}
