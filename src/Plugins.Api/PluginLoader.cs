using System.Reflection;

namespace ZBS.Plugins.Api;

/// <summary>Загруженный плагин и откуда он приехал (для списка/диагностики).</summary>
public sealed record LoadedPlugin(IPlugin Plugin, string Source);

/// <summary>
/// Обнаружение и загрузка плагинов. Изоляция ошибок — закон: битая сборка или падение
/// в конструкторе одного плагина НЕ роняет загрузку остальных и не роняет плеер.
/// </summary>
public static class PluginLoader
{
    /// <summary>Найти и создать все IPlugin в готовых сборках (используется и в тестах).</summary>
    public static IReadOnlyList<LoadedPlugin> FromAssemblies(
        IEnumerable<Assembly> assemblies, Action<string>? onError = null)
    {
        var result = new List<LoadedPlugin>();
        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                onError?.Invoke($"{asm.GetName().Name}: часть типов не загрузилась ({ex.Message})");
            }
            catch (Exception ex)
            {
                onError?.Invoke($"{asm.GetName().Name}: {ex.Message}");
                continue;
            }

            foreach (var type in types)
            {
                if (!IsPluginType(type)) continue;
                try
                {
                    if (Activator.CreateInstance(type) is IPlugin plugin)
                        result.Add(new LoadedPlugin(plugin, asm.GetName().Name ?? "?"));
                }
                catch (Exception ex)
                {
                    // Падение одного плагина — не беда для остальных.
                    onError?.Invoke($"{type.FullName}: не создался ({ex.InnerException?.Message ?? ex.Message})");
                }
            }
        }
        return result;
    }

    /// <summary>Загрузить плагины из папки (*.dll). Нет папки — пустой список, не ошибка.</summary>
    public static IReadOnlyList<LoadedPlugin> FromDirectory(string directory, Action<string>? onError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<LoadedPlugin>();

        var loaded = new List<LoadedPlugin>();
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                loaded.AddRange(FromAssemblies(new[] { asm }, onError));
            }
            catch (Exception ex)
            {
                onError?.Invoke($"{Path.GetFileName(dll)}: не загрузилась ({ex.Message})");
            }
        }
        return loaded;
    }

    private static bool IsPluginType(Type t) =>
        t is { IsClass: true, IsAbstract: false } &&
        typeof(IPlugin).IsAssignableFrom(t) &&
        t.GetConstructor(Type.EmptyTypes) is not null;
}
