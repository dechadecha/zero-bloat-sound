using Avalonia.Threading;
using ZBS.Library;
using ZBS.UI.Desktop.Localization;

namespace ZBS.UI.Desktop.ViewModels;

/// <summary>
/// Строка списка «Все треки»: номер, трек и флаг «сейчас играет» (полоски вместо номера).
/// Playing мутабелен с INPC: при смене трека обновляются ДВЕ строки, а не пересоздаются 3764 —
/// иначе ListBox сбрасывал выделение и прокрутку на каждый переход.
/// </summary>
public sealed class LibRow : ViewModelBase
{
    private bool _playing;

    public LibRow(int n, LibraryTrack t, bool playing)
    {
        N = n;
        T = t;
        _playing = playing;
    }

    public int N { get; }
    public LibraryTrack T { get; }

    public bool Playing
    {
        get => _playing;
        set { if (Set(ref _playing, value)) Raise(nameof(NotPlaying)); }
    }

    public bool NotPlaying => !_playing;
}

/// <summary>Узел-артист в дереве медиатеки (Артист → Альбомы → треки).</summary>
public sealed class ArtistNode : ViewModelBase
{
    private bool _expanded;
    public ArtistNode(string name, IReadOnlyList<AlbumNode> albums)
    {
        Name = name;
        Albums = albums;
        TrackCount = albums.Sum(a => a.Tracks.Count);
    }
    public string Name { get; }
    public IReadOnlyList<AlbumNode> Albums { get; }
    public int TrackCount { get; }
    public string Header => $"{Name}  ·  {TrackCount}";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>Узел-альбом: список треков.</summary>
public sealed class AlbumNode : ViewModelBase
{
    private bool _expanded;
    public AlbumNode(string name, IReadOnlyList<LibraryTrack> tracks)
    {
        Name = name;
        Tracks = tracks;
    }
    public string Name { get; }
    public IReadOnlyList<LibraryTrack> Tracks { get; }
    public string Header => $"{Name}  ·  {Tracks.Count}";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>Панель медиатеки: папки, скан, живой поиск, браузинг, дубликаты/битые, рейтинги.</summary>
public sealed class LibraryViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private string _query = "";
    private string _status = "";
    private int _selectedIndex = -1;
    private int _selectedFolderIndex = -1;
    private int _selectedArtistIndex;
    private int _selectedGenreIndex;
    private int _searchGeneration;
    private CancellationTokenSource? _searchDebounce;
    private CancellationTokenSource? _scanCts;

    public Action<IReadOnlyList<LibraryTrack>, int>? PlayRequested { get; set; }
    public Action<IReadOnlyList<LibraryTrack>>? AppendRequested { get; set; }
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>Клик по треку в списке → карточка-деталь (вешает MainViewModel).</summary>
    public Action<LibraryTrack>? DetailRequested { get; set; }

    private bool _ratedOnly;
    /// <summary>Раздел «Оценки»: показывать только треки со звёздами.</summary>
    public bool RatedOnly
    {
        get => _ratedOnly;
        set { if (Set(ref _ratedOnly, value)) _ = RunSearchAsync(); }
    }

    /// <summary>Проба файла декодером (для сканера битых) — приходит от аудио-бэкенда через App.</summary>
    public Func<string, bool>? FileProber { get; set; }

    public IReadOnlyList<LibraryTrack> Results { get; private set; } = Array.Empty<LibraryTrack>();

    /// <summary>Те же Results, но с номерами и отметкой играющего — для списка «Все треки».</summary>
    public IReadOnlyList<LibRow> Rows { get; private set; } = Array.Empty<LibRow>();
    private string? _playingId;

    /// <summary>Индекс играющего трека в Rows (−1 — нет/не в списке). Для автопрокрутки «Все треки».</summary>
    public int PlayingRowIndex { get; private set; } = -1;

    /// <summary>Отметить играющий трек (Track.ResumeKey == LibraryTrack.Id). Зовёт MainViewModel при смене трека.</summary>
    public void SetPlayingId(string? id)
    {
        if (_playingId == id) return;
        _playingId = id;
        for (var i = 0; i < Rows.Count; i++)
            Rows[i].Playing = Rows[i].T.Id == id; // точечно: коллекция не подменяется, выделение живо
        UpdatePlayingRowIndex();
    }

    private void RebuildRows()
    {
        Rows = Results.Select((t, i) => new LibRow(i + 1, t, t.Id == _playingId)).ToList();
        UpdatePlayingRowIndex();
        Raise(nameof(Rows));
        RebuildTree();
    }

    // ---- Дерево Артист → Альбом → треки ----

    public IReadOnlyList<ArtistNode> Tree { get; private set; } = Array.Empty<ArtistNode>();

    private bool _showTree = true;
    /// <summary>Режим отображения: дерево (по умолчанию) или плоский список.</summary>
    public bool ShowTree
    {
        get => _showTree;
        set { if (Set(ref _showTree, value)) { Raise(nameof(ShowFlat)); Raise(nameof(TreeToggleText)); _ = RunSearchAsync(); } }
    }
    public bool ShowFlat => !_showTree;
    public string TreeToggleText => _showTree ? "☰ Дерево" : "≣ Список";

    private static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s!.Trim();

    private void RebuildTree()
    {
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        Tree = Results
            .GroupBy(t => Norm(t.Artist))
            .OrderBy(g => g.Key, cmp)
            .Select(g => new ArtistNode(g.Key,
                g.GroupBy(t => Norm(t.Album))
                 .OrderBy(a => a.Key, cmp)
                 .Select(a => new AlbumNode(a.Key,
                     a.OrderBy(x => x.Title ?? x.Display, cmp).ToList()))
                 .ToList()))
            .ToList();
        // При активном поиске с немногими попаданиями — раскрываем, чтобы найденное было сразу видно.
        if (!string.IsNullOrWhiteSpace(Query) && Tree.Count <= 15)
            foreach (var ar in Tree)
            {
                ar.IsExpanded = true;
                if (ar.Albums.Count <= 6) foreach (var al in ar.Albums) al.IsExpanded = true;
            }
        Raise(nameof(Tree));
    }

    /// <summary>Двойной клик по треку в дереве — играть его альбом с этого места.</summary>
    public void PlayTreeTrack(LibraryTrack track)
    {
        foreach (var ar in Tree)
            foreach (var al in ar.Albums)
                for (var i = 0; i < al.Tracks.Count; i++)
                    if (al.Tracks[i].Id == track.Id)
                    {
                        PlayRequested?.Invoke(al.Tracks, i);
                        return;
                    }
    }

    private void UpdatePlayingRowIndex()
    {
        PlayingRowIndex = -1;
        if (_playingId is null) return;
        for (var i = 0; i < Rows.Count; i++)
            if (Rows[i].T.Id == _playingId) { PlayingRowIndex = i; break; }
    }
    public IReadOnlyList<string> Folders { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> Artists { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> Genres { get; private set; } = Array.Empty<string>();

    public AsyncRelayCommand AddFolderCommand { get; }
    public AsyncRelayCommand RemoveFolderCommand { get; }
    public AsyncRelayCommand RescanCommand { get; }
    public RelayCommand AppendAllCommand { get; }
    public AsyncRelayCommand DuplicatesCommand { get; }
    public AsyncRelayCommand BrokenCommand { get; }
    public ParamRelayCommand RateCommand { get; }

    public LibraryViewModel(LibraryService library)
    {
        _library = library;
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, ex => Status = ex.Message);
        RemoveFolderCommand = new AsyncRelayCommand(RemoveFolderAsync, ex => Status = ex.Message);
        RescanCommand = new AsyncRelayCommand(() => ScanAsync(), ex => Status = ex.Message);
        AppendAllCommand = new RelayCommand(() =>
        {
            if (Results.Count > 0) AppendRequested?.Invoke(Results);
        });
        DuplicatesCommand = new AsyncRelayCommand(ShowDuplicatesAsync, ex => Status = ex.Message);
        BrokenCommand = new AsyncRelayCommand(ShowBrokenAsync, ex => Status = ex.Message);
        RateCommand = new ParamRelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out var rating))
                RateSelected(rating);
        });
        RefreshFolders();
        RefreshFilters();
        UpdateStatus();
        _ = RunSearchAsync();
    }

    public string LibraryTitle => Loc.T("LibraryTab");
    public string AddFolderText => Loc.T("AddFolder");
    public string RemoveFolderText => Loc.T("RemoveFolder");
    public string RescanText => Loc.T("Rescan");
    public string AppendAllText => Loc.T("AppendAll");
    public string SearchWatermark => Loc.T("SearchLibrary");
    public string EmptyLibraryHint => Loc.T("EmptyLibraryHint");
    public string DuplicatesText => Loc.T("Duplicates");
    public string BrokenText => Loc.T("Broken");
    public string RateText => Loc.T("Rate");
    public bool HasFolders => Folders.Count > 0;

    public string Status { get => _status; private set => Set(ref _status, value); }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (!Set(ref _selectedIndex, value)) return;
            if (value >= 0 && value < Results.Count)
                DetailRequested?.Invoke(Results[value]);
        }
    }
    public int SelectedFolderIndex { get => _selectedFolderIndex; set => Set(ref _selectedFolderIndex, value); }

    public int SelectedArtistIndex
    {
        get => _selectedArtistIndex;
        set { if (Set(ref _selectedArtistIndex, value)) _ = RunSearchAsync(); }
    }

    public int SelectedGenreIndex
    {
        get => _selectedGenreIndex;
        set
        {
            if (!Set(ref _selectedGenreIndex, value)) return;
            Raise(nameof(GenreLabel));
            _ = RunSearchAsync();
        }
    }

    /// <summary>Подпись кнопки-дропдауна «Жанр: все ▾» над списком (канон — не плашки).</summary>
    public string GenreLabel =>
        SelectedGenreIndex > 0 && SelectedGenreIndex < Genres.Count
            ? Genres[SelectedGenreIndex]
            : Loc.T("GenreAll");

    /// <summary>Счётчик в шапке списка: «Треков: 3764».</summary>
    public string CountText => Loc.T("TracksCount", _library.TrackCount);

    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value)) return;
            DebounceSearch();
        }
    }

    private string? CurrentArtistFilter =>
        SelectedArtistIndex > 0 && SelectedArtistIndex < Artists.Count ? Artists[SelectedArtistIndex] : null;

    private string? CurrentGenreFilter =>
        SelectedGenreIndex > 0 && SelectedGenreIndex < Genres.Count ? Genres[SelectedGenreIndex] : null;

    public void PlayFromSelected()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Results.Count)
            PlayRequested?.Invoke(Results, SelectedIndex);
    }

    /// <summary>Оценка выделенного трека (0 — снять). Из контекст-меню.</summary>
    public void RateSelected(int rating)
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        var track = Results[SelectedIndex];
        _library.SetRating(track.Id, rating);
        _ = RunSearchAsync(); // перечитать, чтобы звёзды обновились
    }

    public void CancelScan() => _scanCts?.Cancel();

    private void DebounceSearch()
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        _ = Task.Delay(250, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Dispatcher.UIThread.Post(() => _ = RunSearchAsync());
        }, TaskScheduler.Default);
    }

    private async Task RunSearchAsync()
    {
        var generation = ++_searchGeneration;
        var query = Query;
        var artist = CurrentArtistFilter;
        var genre = CurrentGenreFilter;
        var ratedOnly = RatedOnly;
        var keepId = SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex].Id : null;
        IReadOnlyList<LibraryTrack> found;
        try
        {
            found = await Task.Run(() =>
            {
                // «Оценки»: фильтр ДО лимита не выразить текущим Search — берём всё и фильтруем,
                // иначе LIMIT 500 съедал оценённые треки с конца алфавита.
                // Дерево группирует и виртуализирует — грузим все совпадения; плоский список — 500.
                var limit = ratedOnly || _showTree ? int.MaxValue : 500;
                var results = _library.Search(query, artist, genre, limit);
                return ratedOnly ? results.Where(t => t.Rating > 0).ToList() : (IReadOnlyList<LibraryTrack>)results;
            });
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration) Status = ex.Message; // поиск не должен умирать молча
            return;
        }
        if (generation != _searchGeneration) return;
        Results = found;
        Raise(nameof(Results));
        RebuildRows();
        // Вернуть выделение на тот же трек: после оценки/пересканирования список подменился.
        if (keepId is not null)
        {
            var restored = -1;
            for (var i = 0; i < found.Count; i++)
                if (found[i].Id == keepId) { restored = i; break; }
            SelectedIndex = restored;
        }
        UpdateStatus();
    }

    private async Task ShowDuplicatesAsync()
    {
        var generation = ++_searchGeneration;
        var found = await Task.Run(() => _library.FindDuplicates());
        if (generation != _searchGeneration) return;
        Results = found;
        Raise(nameof(Results));
        RebuildRows();
        Status = Loc.T("DuplicatesFound", found.Count);
    }

    private async Task ShowBrokenAsync()
    {
        if (FileProber is null) return;
        var generation = ++_searchGeneration;
        _scanCts?.Cancel(); // сканер битых отменяем тем же CancelScan, что и обычный скан
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        Status = Loc.T("Scanning");
        IReadOnlyList<LibraryTrack> found;
        try
        {
            found = await Task.Run(() => _library.FindBroken(
                FileProber,
                processed => Dispatcher.UIThread.Post(() => Status = Loc.T("ScanProgress", processed)),
                cts.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (generation != _searchGeneration) return;
        Results = found;
        Raise(nameof(Results));
        RebuildRows(); // Rows и Results обязаны совпадать — иначе клики играют чужие треки
        Status = Loc.T("BrokenFound", found.Count);
    }

    private async Task AddFolderAsync()
    {
        if (FolderPicker is null) return;
        var folder = await FolderPicker();
        if (folder is null) return;
        _library.AddFolder(folder);
        RefreshFolders();
        await ScanAsync();
    }

    private async Task RemoveFolderAsync()
    {
        if (SelectedFolderIndex < 0 || SelectedFolderIndex >= Folders.Count) return;
        var folder = Folders[SelectedFolderIndex];
        await Task.Run(() => _library.RemoveFolder(folder));
        RefreshFolders();
        RefreshFilters();
        await RunSearchAsync();
    }

    private Task? _scanFlow;

    /// <summary>
    /// Сканы сериализуются цепочкой: новый вход отменяет текущий, ЖДЁТ его выхода и лишь
    /// потом стартует свой. Иначе LibraryService видел «уже сканирую» и молча выходил
    /// (клик «Пересканировать» посреди скана убивал скан), а два конкурентных входа
    /// (Rescan + AddFolder) дрались за _scanCts.
    /// </summary>
    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        var previous = _scanFlow;
        var flow = ScanCoreAsync(previous);
        _scanFlow = flow;
        try
        {
            await flow;
        }
        finally
        {
            if (_scanFlow == flow) _scanFlow = null;
        }
    }

    private async Task ScanCoreAsync(Task? previous)
    {
        if (previous is not null)
        {
            try { await previous; }
            catch (Exception) { /* исход предыдущего скана нас не касается */ }
        }
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        Status = Loc.T("Scanning");
        try
        {
            await Task.Run(() => _library.Scan(
                processed => Dispatcher.UIThread.Post(() => Status = Loc.T("ScanProgress", processed)),
                cts.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return; // нас уже сменил следующий скан
        RefreshFilters();
        await RunSearchAsync();
    }

    private void RefreshFolders()
    {
        Folders = _library.Folders;
        Raise(nameof(Folders));
        Raise(nameof(HasFolders));
    }

    private void RefreshFilters()
    {
        var artists = new List<string> { Loc.T("AllArtists") };
        artists.AddRange(_library.GetArtists());
        Artists = artists;
        var genres = new List<string> { Loc.T("AllGenres") };
        genres.AddRange(_library.GetGenres());
        Genres = genres;
        _selectedArtistIndex = 0;
        _selectedGenreIndex = 0;
        Raise(nameof(Artists));
        Raise(nameof(Genres));
        Raise(nameof(SelectedArtistIndex));
        Raise(nameof(SelectedGenreIndex));
        Raise(nameof(GenreLabel));
        Raise(nameof(CountText));
    }

    private void UpdateStatus()
    {
        var status = Loc.T("LibraryCount", _library.TrackCount);
        if (_library.LastScanSkippedCleanup)
            status += " · " + Loc.T("FolderUnavailable");
        Status = status;
        Raise(nameof(CountText));
    }
}
