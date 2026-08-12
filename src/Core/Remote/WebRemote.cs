using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ZBS.Core.Remote;

/// <summary>
/// Локальный веб-пульт: крошечный HTTP-сервер на TcpListener (HttpListener на Windows
/// требует админских urlacl для LAN — обходим). Только LAN, только с PIN, по умолчанию выключен.
/// Команды исполняются через делегаты — сервер ничего не знает о плеере.
/// </summary>
public sealed class WebRemote : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _pinFails;
    private DateTime _pinLockUntil = DateTime.MinValue;
    private readonly SemaphoreSlim _clientSlots = new(16); // потолок одновременных соединений

    /// <summary>PIN из ссылки (/p/1234/...). Без совпадения — 403.</summary>
    public string Pin { get; private set; } = "";

    public int Port { get; private set; }

    /// <summary>JSON-статус плеера (зовётся на UI-потоке через маршалер владельца).</summary>
    public Func<string>? StatusProvider { get; set; }

    /// <summary>Команда пульта: play/pause/next/prev/stop/volume/seek/radio/podcast (+ аргумент). true — ок.</summary>
    public Func<string, string?, bool>? CommandHandler { get; set; }

    /// <summary>Списки контента для вкладок пульта: what (+ query) → JSON.</summary>
    public Func<string, string, string>? ListProvider { get; set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Ссылка для телефона (первый не-loopback IPv4).</summary>
    public string? Link
    {
        get
        {
            if (!IsRunning) return null;
            var ip = LocalIp();
            return ip is null ? null : $"http://{ip}:{Port}/p/{Pin}/";
        }
    }

    /// <summary>Все ссылки пульта — по одной на каждый сетевой интерфейс (Ethernet, Wi-Fi…).
    /// Телефон должен быть в той же сети роутера, Wi-Fi на самом ПК не обязателен.</summary>
    public IReadOnlyList<string> Links
    {
        get
        {
            if (!IsRunning) return Array.Empty<string>();
            var ips = new List<string>();
            try
            {
                // Только интерфейсы с default gateway = реальная сеть роутера.
                // Виртуальные адаптеры (WSL/Docker/Hyper-V, 172.18.* и пр.) шлюза не имеют —
                // их адреса с телефона недостижимы и только путают.
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    // Туннели/виртуалки по именам: VPN может прописать себе и шлюз,
                    // так что одного gateway-фильтра мало (happ-tun у Дениса — живой пример).
                    var label = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    if (label.Contains("tun") || label.Contains("tap") || label.Contains("vpn")
                        || label.Contains("wsl") || label.Contains("docker") || label.Contains("virtual")
                        || label.Contains("vethernet") || label.Contains("hyper-v")) continue;
                    var props = ni.GetIPProperties();
                    if (!props.GatewayAddresses.Any(g =>
                            g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = addr.Address.ToString();
                        if (s.StartsWith("169.254")) continue; // link-local мусор
                        if (!ips.Contains(s)) ips.Add(s);
                    }
                }
                // Домашние диапазоны вперёд: 192.168.* (типичный роутер) → 10.* → остальное.
                // Первая ссылка уходит в QR — она должна быть самой вероятной.
                static int Score(string ip) =>
                    ip.StartsWith("192.168.") ? 0 : ip.StartsWith("10.") ? 1 : ip.StartsWith("172.") ? 3 : 2;
                ips = ips.OrderBy(Score).ToList();
            }
            catch (Exception) { }
            // Совсем ничего со шлюзом (экзотика) — фолбэк на исходящий интерфейс.
            if (ips.Count == 0 && LocalIp() is { } primary) ips.Add(primary);
            return ips.Select(ip => $"http://{ip}:{Port}/p/{Pin}/").ToList();
        }
    }

    /// <summary>Лучший LAN-адрес машины (тот же выбор, что для ссылок пульта) — нужен и кастингу.</summary>
    public static string? BestLanIp()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var label = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (label.Contains("tun") || label.Contains("tap") || label.Contains("vpn")
                    || label.Contains("wsl") || label.Contains("docker") || label.Contains("virtual")
                    || label.Contains("vethernet") || label.Contains("hyper-v")) continue;
                var props = ni.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = addr.Address.ToString();
                    if (!s.StartsWith("169.254") && !ips.Contains(s)) ips.Add(s);
                }
            }
        }
        catch (Exception) { }
        static int Score(string ip) =>
            ip.StartsWith("192.168.") ? 0 : ip.StartsWith("10.") ? 1 : ip.StartsWith("172.") ? 3 : 2;
        return ips.OrderBy(Score).FirstOrDefault() ?? LocalIp();
    }

    private static string? LocalIp()
    {
        try
        {
            // UDP-«коннект» наружу не шлёт пакетов, но выбирает исходящий интерфейс.
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool Start(int port = 8973)
    {
        Stop();
        try
        {
            Pin = Random.Shared.Next(1000, 10000).ToString();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = port;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(_listener, _cts.Token);
            TryOpenFirewall(port);
            return true;
        }
        catch (Exception)
        {
            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _listener?.Stop(); } catch (Exception) { }
        _listener = null;
    }

    /// <summary>Пробуем открыть входящий порт в брандмауэре (сработает только под админом —
    /// без него Windows сама покажет диалог «Разрешить доступ» при первом запуске).</summary>
    private static void TryOpenFirewall(int port)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Правило уже есть — не плодим дубликаты (netsh не дедуплицирует сам).
            var check = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=\"Zero-Bloat Sound пульт\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (var c = System.Diagnostics.Process.Start(check))
            {
                c?.WaitForExit(3000);
                if (c?.ExitCode == 0) return; // найдено
            }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                // Только домашние/доменные профили: на публичных сетях порт не открываем.
                Arguments = "advfirewall firewall add rule name=\"Zero-Bloat Sound пульт\" " +
                            $"dir=in action=allow protocol=TCP localport={port} profile=private,domain",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception) { }
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) { return; } // Stop() или отмена
            if (!await _clientSlots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                client.Dispose(); // все слоты заняты (флуд) — connection reset вместо накопления
                continue;
            }
            _ = HandleClient(client, ct).ContinueWith(_ => _clientSlots.Release());
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                // Пульт — строго локальная сеть: внешние адреса отбрасываем.
                if (client.Client.RemoteEndPoint is IPEndPoint ep && !IsPrivate(ep.Address))
                    return;

                // ReceiveTimeout сокета не действует на async-чтения: молчащий клиент
                // (slowloris) висел бы вечно — жёсткий таймаут на весь запрос.
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromSeconds(10));
                var rct = reqCts.Token;

                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(rct).ConfigureAwait(false);
                if (requestLine is null || requestLine.Length > 4096) return;
                // Заголовки дочитываем и выбрасываем (максимум 100 строк — дальше это не браузер).
                for (var i = 0; i < 100; i++)
                {
                    var header = await reader.ReadLineAsync(rct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(header)) break;
                    if (header.Length > 8192) return;
                }

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || parts[0] != "GET")
                {
                    await Respond(stream, "405 Method Not Allowed", "text/plain", "GET only", rct).ConfigureAwait(false);
                    return;
                }

                await Route(stream, parts[1], rct).ConfigureAwait(false);
            }
            catch (Exception) { /* оборванный клиент — не наша проблема */ }
        }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false; // IPv6 (кроме loopback) не пускаем
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    private async Task Route(NetworkStream stream, string path, CancellationToken ct)
    {
        // Формат: /p/<pin>/            — страница пульта
        //         /p/<pin>/api/status  — JSON-статус
        //         /p/<pin>/api/cmd?name=play&arg=...
        var segments = path.Split('?');
        var route = segments[0];
        var query = segments.Length > 1 ? segments[1] : "";

        if (!route.StartsWith("/p/", StringComparison.Ordinal))
        {
            await Respond(stream, "403 Forbidden", "text/plain", "PIN required", ct).ConfigureAwait(false);
            return;
        }
        var rest = route[3..];
        var slash = rest.IndexOf('/');
        var pin = slash < 0 ? rest : rest[..slash];
        // Анти-брутфорс: 4-значный PIN перебирается за секунды, если отвечать мгновенно.
        // Задержка на каждый промах + блокировка после серии делают перебор бессмысленным.
        if (DateTime.UtcNow < _pinLockUntil)
        {
            await Respond(stream, "429 Too Many Requests", "text/plain", "locked", ct).ConfigureAwait(false);
            return;
        }
        if (pin != Pin)
        {
            if (Interlocked.Increment(ref _pinFails) >= 15)
            {
                _pinLockUntil = DateTime.UtcNow.AddMinutes(5);
                Interlocked.Exchange(ref _pinFails, 0);
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
            await Respond(stream, "403 Forbidden", "text/plain", "wrong PIN", ct).ConfigureAwait(false);
            return;
        }
        Interlocked.Exchange(ref _pinFails, 0);
        var sub = slash < 0 ? "" : rest[(slash + 1)..];

        switch (sub)
        {
            case "" or "index.html":
                await Respond(stream, "200 OK", "text/html; charset=utf-8", RemotePage.Html, ct).ConfigureAwait(false);
                break;
            case "api/status":
                var json = StatusProvider?.Invoke() ?? "{}";
                await Respond(stream, "200 OK", "application/json; charset=utf-8", json, ct).ConfigureAwait(false);
                break;
            case "api/list":
                var listJson = ListProvider?.Invoke(QueryValue(query, "what") ?? "", query) ?? "{}";
                await Respond(stream, "200 OK", "application/json; charset=utf-8", listJson, ct).ConfigureAwait(false);
                break;
            case "api/cmd":
                string? name = null, arg = null;
                foreach (var kv in query.Split('&'))
                {
                    var eq = kv.IndexOf('=');
                    if (eq < 0) continue;
                    var k = kv[..eq];
                    var v = Uri.UnescapeDataString(kv[(eq + 1)..]);
                    if (k == "name") name = v;
                    else if (k == "arg") arg = v;
                }
                var ok = name is not null && (CommandHandler?.Invoke(name, arg) ?? false);
                await Respond(stream, ok ? "200 OK" : "400 Bad Request",
                    "application/json", ok ? "{\"ok\":true}" : "{\"ok\":false}", ct).ConfigureAwait(false);
                break;
            default:
                await Respond(stream, "404 Not Found", "text/plain", "not found", ct).ConfigureAwait(false);
                break;
        }
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var kv in query.Split('&'))
        {
            var eq = kv.IndexOf('=');
            if (eq > 0 && kv[..eq] == key) return Uri.UnescapeDataString(kv[(eq + 1)..]);
        }
        return null;
    }

    private static async Task Respond(NetworkStream stream, string status, string contentType, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}
