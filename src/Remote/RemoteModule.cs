namespace ZBS.Remote;

/// <summary>
/// Заглушка модуля пульта (M5): локальный HTTP API (Kestrel, только LAN),
/// веб-пульт (QR + ссылка, PIN), /stats JSON, headless-режим (v1.2).
/// </summary>
public static class RemoteModule
{
    public const string Name = "Remote";
}
