# ZBS Plugin: Obsidian

Пример плагина поверх [ZBS Plugin API](../Plugins.Api) (`IGeneralPlugin`).
Ведёт журнал прослушивания в заметку [Obsidian](https://obsidian.md): на смену трека
дописывает строку в markdown-файл.

## Как подключить

1. Собрать: `dotnet build src/Plugins.Obsidian` → `ZBS.Plugins.Obsidian.dll`.
2. Положить dll в папку `plugins` рядом с приложением.
3. Рядом с dll создать `obsidian.json` (шаблон — `obsidian.example.json`):
   ```json
   { "note": "C:\\Users\\me\\Obsidian\\Vault\\Музыка.md", "format": "- {time} 🎵 {artist} — {title}" }
   ```
   Либо `"vault"` вместо `note` — тогда запись идёт в ежедневную заметку `yyyy-MM-dd.md`.
4. В приложении: **Настройки → Интеграции → Плагины** — включить.

Плейсхолдеры формата: `{date}` `{time}` `{artist}` `{title}`. `dedupe` — не дублировать один трек подряд.

## Как основа для своего плагина

Минимум: класс с публичным конструктором без аргументов, реализующий `IGeneralPlugin`
(`Id` / `Name` / `Version` + `OnLoad(IPluginHost)` / `OnUnload()`). В `OnLoad` подписаться на
`host.TrackChanged` / `host.PlayingChanged`. Ссылаться только на `ZBS.Plugins.Api`.
