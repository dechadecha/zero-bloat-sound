namespace ZBS.Core.Podcasts;

/// <summary>Подписка на подкаст (RSS-фид).</summary>
public sealed record PodcastFeed(string FeedUrl, string Title, string Author, string? ImageUrl);

/// <summary>Эпизод подкаста. AudioUrl — enclosure из RSS.</summary>
public sealed record PodcastEpisode(
    string FeedUrl,
    string Guid,
    string Title,
    string AudioUrl,
    DateTime? Published,
    double DurationSeconds,
    string? Description)
{
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(2);
            if (Published is { } p) parts.Add(p.ToString("d MMMM yyyy"));
            if (DurationSeconds > 0)
            {
                var ts = TimeSpan.FromSeconds(DurationSeconds);
                parts.Add(ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss"));
            }
            return string.Join(" · ", parts);
        }
    }
}
