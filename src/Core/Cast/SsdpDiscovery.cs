using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace ZBS.Core.Cast;

/// <summary>
/// Поиск DLNA-рендереров: SSDP M-SEARCH (UDP multicast 239.255.255.250:1900),
/// затем описание устройства по LOCATION и выуживание control-URL сервисов.
/// Честный discovery: что ответило — то и показываем, с реальными ошибками.
/// </summary>
public static class SsdpDiscovery
{
    private const string SearchTarget = "urn:schemas-upnp-org:device:MediaRenderer:1";

    public static async Task<IReadOnlyList<DlnaDevice>> FindRenderersAsync(TimeSpan timeout, CancellationToken ct)
    {
        var locations = await SearchLocationsAsync(timeout, ct).ConfigureAwait(false);
        var devices = new List<DlnaDevice>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        foreach (var location in locations)
        {
            try
            {
                var device = await DescribeAsync(http, location, ct).ConfigureAwait(false);
                if (device is not null && devices.All(d => d.AvTransportUrl != device.AvTransportUrl))
                    devices.Add(device);
            }
            catch (Exception) { /* кривое описание — пропускаем устройство */ }
        }
        return devices;
    }

    private static async Task<IReadOnlyList<string>> SearchLocationsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {SearchTarget}\r\n\r\n");

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        // Шлём трижды (UDP теряется), собираем ответы до таймаута.
        for (var i = 0; i < 3; i++)
            await udp.SendAsync(request, request.Length, multicast).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var remain = deadline - DateTime.UtcNow;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(remain);
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            var text = Encoding.ASCII.GetString(result.Buffer);
            foreach (var line in text.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    found.Add(l[9..].Trim());
            }
        }
        return found.ToList();
    }

    /// <summary>Читает description.xml устройства → имя + control-URL AVTransport/RenderingControl.</summary>
    private static async Task<DlnaDevice?> DescribeAsync(HttpClient http, string location, CancellationToken ct)
    {
        var xmlText = await http.GetStringAsync(location, ct).ConfigureAwait(false);
        var doc = new XmlDocument();
        using (var reader = XmlReader.Create(new StringReader(xmlText), new XmlReaderSettings
               {
                   DtdProcessing = DtdProcessing.Ignore,
                   XmlResolver = null,
               }))
        {
            doc.Load(reader);
        }
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("d", "urn:schemas-upnp-org:device-1-0");

        var name = doc.SelectSingleNode("//d:device/d:friendlyName", ns)?.InnerText.Trim()
                   ?? doc.SelectSingleNode("//*[local-name()='friendlyName']")?.InnerText.Trim()
                   ?? new Uri(location).Host;

        string? avUrl = null, rcUrl = null;
        var services = doc.SelectNodes("//*[local-name()='service']");
        if (services is not null)
        {
            foreach (XmlNode svc in services)
            {
                var type = svc.SelectSingleNode("*[local-name()='serviceType']")?.InnerText ?? "";
                var control = svc.SelectSingleNode("*[local-name()='controlURL']")?.InnerText?.Trim();
                if (string.IsNullOrEmpty(control)) continue;
                var absolute = Uri.TryCreate(control, UriKind.Absolute, out var abs)
                    ? abs.ToString()
                    : new Uri(new Uri(location), control).ToString();
                if (type.Contains("AVTransport", StringComparison.OrdinalIgnoreCase)) avUrl ??= absolute;
                else if (type.Contains("RenderingControl", StringComparison.OrdinalIgnoreCase)) rcUrl ??= absolute;
            }
        }
        return avUrl is null ? null : new DlnaDevice(name, location, avUrl, rcUrl);
    }
}
