# libmpv (видео, M4)

`libmpv-2.dll` не хранится в репозитории (114 МБ, лимит GitHub — 100 МБ).

Скачать: https://github.com/shinchiro/mpv-winbuild-cmake/releases —
архив `mpv-dev-x86_64-<дата>.7z`, из него `libmpv-2.dll` → положить в `lib/mpv/win-x64/`.

Без dll плеер работает полностью, видео-файлы просто недоступны
(`MpvVideoBackend.IsAvailable` = false).
