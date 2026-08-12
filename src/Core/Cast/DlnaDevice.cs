namespace ZBS.Core.Cast;

/// <summary>DLNA-рендерер (телевизор/колонка/ресивер), найденный в локальной сети.</summary>
public sealed record DlnaDevice(
    string Name,
    string Location,          // URL описания устройства
    string AvTransportUrl,    // control URL сервиса AVTransport
    string? RenderingControlUrl);
