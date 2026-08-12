namespace ZBS.Core.Radio;

/// <summary>Радиостанция (из каталога radio-browser или избранного).</summary>
public sealed record RadioStation(
    string Uuid,
    string Name,
    string Url,
    string Country,
    string Tags,
    int Bitrate,
    string Codec)
{
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Country)) parts.Add(Country);
            if (Bitrate > 0) parts.Add($"{Bitrate} кбит/с");
            if (!string.IsNullOrWhiteSpace(Codec)) parts.Add(Codec);
            return string.Join(" · ", parts);
        }
    }
}
