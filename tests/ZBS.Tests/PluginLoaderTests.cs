using System.Linq;
using System.Reflection;
using ZBS.Plugins.Api;
using Xunit;

namespace ZBS.Tests;

// Фейковые плагины в тестовой сборке — материал для загрузчика.
public sealed class FakeGeneralPlugin : IGeneralPlugin
{
    public string Id => "test.fake.general";
    public string Name => "Fake General";
    public string Version => "1.0.0";
    public bool Loaded { get; private set; }
    public void OnLoad(IPluginHost host) => Loaded = true;
    public void OnUnload() => Loaded = false;
}

// Падает в конструкторе — загрузчик обязан это пережить и не потерять остальные.
public sealed class ExplodingPlugin : IPlugin
{
    public ExplodingPlugin() => throw new System.InvalidOperationException("boom");
    public string Id => "test.explode";
    public string Name => "Boom";
    public string Version => "0.0.0";
}

// Абстрактный/без пустого конструктора — не должен инстанцироваться.
public abstract class AbstractPlugin : IPlugin
{
    public string Id => "test.abstract";
    public string Name => "Abstract";
    public string Version => "0";
}

public class PluginLoaderTests
{
    [Fact]
    public void FromAssemblies_finds_concrete_plugins()
    {
        var loaded = PluginLoader.FromAssemblies(new[] { Assembly.GetExecutingAssembly() });
        Assert.Contains(loaded, p => p.Plugin is FakeGeneralPlugin);
    }

    [Fact]
    public void FromAssemblies_skips_abstract_and_survives_throwing_ctor()
    {
        var errors = new System.Collections.Generic.List<string>();
        var loaded = PluginLoader.FromAssemblies(new[] { Assembly.GetExecutingAssembly() }, errors.Add);

        Assert.DoesNotContain(loaded, p => p.Plugin.GetType() == typeof(AbstractPlugin));
        Assert.DoesNotContain(loaded, p => p.Plugin.GetType() == typeof(ExplodingPlugin));
        Assert.Contains(errors, e => e.Contains("boom")); // падение изолировано и залогировано
        Assert.Contains(loaded, p => p.Plugin is FakeGeneralPlugin); // остальные не потеряны
    }

    [Fact]
    public void FromDirectory_missing_folder_is_empty_not_error()
    {
        var loaded = PluginLoader.FromDirectory(Path.Combine(Path.GetTempPath(), "zbs-no-such-dir-xyz"));
        Assert.Empty(loaded);
    }

    [Fact]
    public void Loaded_general_plugin_lifecycle_runs()
    {
        var p = new FakeGeneralPlugin();
        Assert.False(p.Loaded);
        p.OnLoad(new StubHost());
        Assert.True(p.Loaded);
        p.OnUnload();
        Assert.False(p.Loaded);
    }

#pragma warning disable CS0067 // события интерфейса в стабе не поднимаются — это норма для теста
    private sealed class StubHost : IPluginHost
    {
        public void Log(string message) { }
        public string HostVersion => "test";
        public PluginTrackInfo? CurrentTrack => null;
        public event System.Action<PluginTrackInfo?>? TrackChanged;
        public event System.Action<bool>? PlayingChanged;
    }
#pragma warning restore CS0067
}
