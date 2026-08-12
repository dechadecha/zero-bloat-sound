using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ZBS.UI.Desktop.Controls;
using ZBS.UI.Desktop.ViewModels;

namespace ZBS.UI.Desktop.Views;

/// <summary>Только связка вьюхи с VM: диалоги, drag&drop, активация, размер окна, режимы. Логики здесь нет.</summary>
public partial class MainWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    // Компакт включается размером клиентской области (канон §11: и ширина, И высота).
    private const double CompactWidth = 560;
    private const double CompactHeight = 430;
    // Рельс: сайдбар ужат ниже этого — подписи прячутся, остаются иконки.
    private const double RailThreshold = 140;

    private double _savedSideWidth = 230;
    private bool _sidebarCollapsed;

    // Компакт: чем вызван («width»/«height») — от этого зависит условие выхода;
    // при входе по ширине высота окна схлопывается сама (слово Дениса), при выходе — возвращается.
    private string? _compactBy;
    private double _savedHeight = 640;
    private double _savedWidth = 940;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: false);
        DataContextChanged += (_, _) =>
        {
            if (Vm is null) return;
            Width = Vm.InitialWidth;
            Height = Vm.InitialHeight;
            Vm.FolderPicker = PickFolderAsync;
            Vm.PlaylistSavePicker = PickPlaylistSaveAsync;
            Vm.SkinFilesPicker = PickSkinFilesAsync;
            if (Vm.Library is { } lib)
                lib.FolderPicker = PickFolderAsync;
            Vm.ActivateRequested += BringToFront;
            Vm.ExpandRequested += ExpandFromCompact;
            // Визуализаторы кормятся FFT из движка через VM.
            VizPreview.SpectrumSource = Vm.GetSpectrumData;
            VizOverview.SpectrumSource = Vm.GetSpectrumData;
            VizCompactBar.SpectrumSource = Vm.GetSpectrumData;
            ApplyVizMode((VizMode)Vm.VisualizerMode); // сохранённый режим отрисовки
            FmScale.StationMhz = Vm.Radio.FmStationFreqs; // точки станций на рисованной шкале
            // M4: HWND видеоповерхности — в mpv (если он появился раньше VM, отдадим сохранённый).
            if (VideoSurface.SurfaceHandle != IntPtr.Zero)
                Vm.VideoSurfaceSetter?.Invoke(VideoSurface.SurfaceHandle);
            Vm.PropertyChanged += (_, a) =>
            {
                if (a.PropertyName is nameof(MainViewModel.IsVideoActive)
                    or nameof(MainViewModel.IsNowPlaying)
                    or nameof(MainViewModel.IsFull)
                    or nameof(MainViewModel.QueueOpen))
                    UpdateVideoOverlay();
            };
            Vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.SidebarVisible))
                    SyncSidebarColumns();
                else if (args.PropertyName == nameof(MainViewModel.Peaks))
                    WaveBar.SetPeaks(Vm?.Peaks); // M3.4: пики в waveform-подложку
            };
            // Караоке (.lrc): подсвеченную строку — в центр панели.
            Vm.KaraokeLineChanged += ScrollKaraokeToLine;
            // Автопрокрутка видимого списка к играющему треку (шаффл/по порядку).
            Vm.ScrollToPlaying += ScrollListsToPlaying;
            // M3.3: скин-окно.
            Vm.SkinOpenRequested += OpenSkinWindow;
            Vm.SkinCloseRequested += CloseSkinWindowProgrammatically;
            SyncCompact();
        };
        Opened += (_, _) => Vm?.OpenSavedSkinIfAny();
        // Закрыли в компакте → сохраняем ДО-компактный размер, иначе 208-высота
        // (и узкая ширина) навсегда прописались бы в настройки и окно вечно
        // открывалось бы компактом.
        Closing += (_, _) =>
        {
            _appClosing = true;
            _skinWindow?.Close(); // иначе живое скин-окно не даст приложению завершиться
            PersistWindowSize();
        };

        // Рельс — по фактической ширине сайдбара (GridSplitter меняет колонку).
        LayoutUpdated += (_, _) =>
        {
            SyncRail();
            // Помним последний размер в Normal: закрытие в фулскрине/максимайзе не должно
            // прописать в настройки размеры монитора.
            if (WindowState == WindowState.Normal && Width > 0 && Height > 0)
            {
                _lastNormalWidth = Width;
                _lastNormalHeight = Height;
            }
        };
        // Магнитное прилипание окна к краям экрана (тумблер в настройках «Внешность»).
        PositionChanged += OnPositionChangedSnap;
        // M4: HWND может создаться и до, и после DataContext — покрываем оба порядка.
        VideoSurface.SurfaceCreated += h => Vm?.VideoSurfaceSetter?.Invoke(h);
        // Правый клик выделяет строку под курсором: иначе контекст-меню оценивало СТАРОЕ выделение.
        LibraryList.AddHandler(PointerPressedEvent, ListRightClickSelect, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerPressedEvent, ListRightClickSelect, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Флайаут жанра закрывается и при клике по УЖЕ выбранному пункту (SelectionChanged тогда молчит).
        GenreList.PointerReleased += (_, _) => GenreBtn.Flyout?.Hide();
        // Клик по визуализатору — следующий режим отрисовки (столбики → волна → пики → кольцо).
        VizPreview.PointerPressed += CycleVizMode;
        VizOverview.PointerPressed += CycleVizMode;
    }

    private void ApplyVizMode(VizMode mode)
    {
        VizPreview.Mode = mode;
        VizOverview.Mode = mode;
        VizCompactBar.Mode = mode;
    }

    private void CycleVizMode(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null) return;
        ApplyVizMode((VizMode)Vm.CycleVisualizerMode());
    }

    private void ListRightClickSelect(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed) return;
        var item = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item is null) return;
        var index = list.IndexFromContainer(item);
        if (index >= 0) list.SelectedIndex = index;
    }

    private bool _snapping;

    private void OnPositionChangedSnap(object? sender, PixelPointEventArgs e)
    {
        if (_snapping || Vm?.MagnetOn != true || WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromVisual(this);
        if (screen is null) return;
        var wa = screen.WorkingArea;
        var scale = screen.Scaling; // масштаб ЭКРАНА: RenderScaling окна отстаёт при переезде между мониторами
        var w = (int)Math.Round((FrameSize?.Width ?? Width) * scale);
        var h = (int)Math.Round((FrameSize?.Height ?? Height) * scale);
        var threshold = (int)(14 * scale);

        var x = e.Point.X;
        var y = e.Point.Y;
        var nx = x;
        var ny = y;
        if (Math.Abs(x - wa.X) <= threshold) nx = wa.X;
        else if (Math.Abs(wa.Right - (x + w)) <= threshold) nx = wa.Right - w;
        if (Math.Abs(y - wa.Y) <= threshold) ny = wa.Y; // AIMP-стайл: прилип к верхней кромке
        else if (Math.Abs(wa.Bottom - (y + h)) <= threshold) ny = wa.Bottom - h;

        if (nx == x && ny == y) return;
        _snapping = true;
        try { Position = new PixelPoint(nx, ny); }
        finally { _snapping = false; }
    }

    // Компакт по фактическому размеру клиентской области (без Rx-подписок).
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            SyncCompact();
            UpdateVideoOverlay();
        }
        else if (change.Property == WindowStateProperty)
        {
            // Свернули — видео не декодим (звук идёт); развернули — вернуть.
            Vm?.SetWindowMinimized(WindowState == WindowState.Minimized);
        }
    }

    /// <summary>
    /// Видеоповерхность: в Обзоре с видео — растянута по контент-зоне (с местом под транспорт
    /// и, при открытой очереди, под её панель); иначе скрыта. IsVisible=false у NativeControlHost
    /// ПРЯЧЕТ нативное окно, не разрушая HWND (0×0 его не сворачивал — оставался чёрный экран).
    /// </summary>
    private void UpdateVideoOverlay()
    {
        if (Vm is null) return;
        var show = Vm.IsVideoActive && Vm.IsNowPlaying && Vm.IsFull;
        VideoSurface.IsVisible = show;
        if (!show) return;
        VideoSurface.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VideoSurface.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        VideoSurface.Width = double.NaN;
        VideoSurface.Height = double.NaN;
        var right = Vm.QueueOpen ? 316 : 24; // очередь-слайдаут не должна тонуть под видео
        VideoSurface.Margin = new Thickness(24, 56, right, 132);
    }

    private void SyncCompact()
    {
        if (Vm is null) return;
        var w = ClientSize.Width;
        var h = ClientSize.Height;

        if (Vm.IsCompact)
        {
            // Выход: по той же оси, которой вошли.
            if (_compactBy == "width" && w >= CompactWidth)
            {
                Vm.IsCompact = false;
                _compactBy = null;
                MaxHeight = double.PositiveInfinity;
                Height = Math.Max(_savedHeight, 520); // вернуть нормальную высоту
            }
            else if (_compactBy == "height" && h >= CompactHeight && w >= CompactWidth)
            {
                Vm.IsCompact = false;
                _compactBy = null;
            }
            return;
        }

        if (w < CompactWidth)
        {
            _compactBy = "width";
            _savedHeight = Height;
            _savedWidth = Math.Max(Width, 940); // до-компактная ширина уже потеряна в драге — берём разумную
            Vm.IsCompact = true;
            // Во время драга Windows возвращает высоту обратно — держим её MaxHeight'ом,
            // чтобы окно не оставалось пустым столбом.
            MaxHeight = 208;
            Height = 208;
        }
        else if (h < CompactHeight)
        {
            _compactBy = "height";
            _savedHeight = Height;
            Vm.IsCompact = true;
        }
    }

    private void SyncRail()
    {
        if (Vm is null || _sidebarCollapsed) return;
        var width = SidebarPane.Bounds.Width;
        if (width <= 0) return;
        var rail = width < RailThreshold;
        if (Vm.SidebarRail != rail) Vm.SidebarRail = rail;
    }

    /// <summary>Обзор/страница настройки — сайдбар схлопывается вместе со своей колонкой.</summary>
    private void SyncSidebarColumns()
    {
        if (Vm is null) return;
        var cols = BodyGrid.ColumnDefinitions;
        if (Vm.SidebarVisible)
        {
            if (!_sidebarCollapsed) return;
            _sidebarCollapsed = false;
            cols[0].MinWidth = 64;
            cols[0].Width = new GridLength(_savedSideWidth);
            cols[1].Width = new GridLength(8);
        }
        else
        {
            if (_sidebarCollapsed) return;
            _sidebarCollapsed = true;
            if (cols[0].Width.IsAbsolute && cols[0].Width.Value > 0)
                _savedSideWidth = cols[0].Width.Value;
            cols[0].MinWidth = 0;
            cols[0].Width = new GridLength(0);
            cols[1].Width = new GridLength(0);
        }
    }

    private void ExpandFromCompact()
    {
        MaxHeight = double.PositiveInfinity;
        Width = Math.Max(Width, 960);
        Height = Math.Max(_savedHeight, 640);
    }

    /// <summary>Выбор жанра в дропдауне — закрыть флайаут (сам выбор уже ушёл в VM биндингом).</summary>
    private void GenreList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        GenreBtn.Flyout?.Hide();

    // ---- M3.3: скин-окно. Скин ЗАМЕНЯЕТ плеер (классика), а не открывается поверх:
    // главное окно прячется, закрытие скина возвращает обычный UI. ----

    private SkinWindow? _skinWindow;
    private bool _appClosing;

    private void OpenSkinWindow(string path)
    {
        if (Vm is null) return;
        try
        {
            var package = ZBS.Skins.SkinPackage.Load(path);
            // Старое окно обнуляем ДО Close(): Closed стреляет синхронно, и его хендлер
            // иначе принял бы смену скина за закрытие пользователем (стирал бы новый SkinFile).
            var old = _skinWindow;
            _skinWindow = null;
            old?.Close();
            var window = new SkinWindow(package, Vm);
            window.Closed += (_, _) =>
            {
                if (_skinWindow != window) return;
                _skinWindow = null;
                if (_appClosing) return; // выход приложения: SkinFile сохраняем — на рестарте скин вернётся
                Vm?.OnSkinWindowClosedByUser();
                Show();
                Activate();
            };
            _skinWindow = window;
            window.Show();
            Hide();
        }
        catch (Exception ex)
        {
            Vm.ReportSkinError(ex.Message);
        }
    }

    /// <summary>Закрытие скин-окна из VM (выключение/смена на тему): состояние скина
    /// VM уже обработала сама, «пользовательскую» логику Closed не запускаем.</summary>
    private void CloseSkinWindowProgrammatically()
    {
        var old = _skinWindow;
        if (old is null) return;
        _skinWindow = null;
        old.Close();
        Show();
        Activate();
    }

    private void Skin_DoubleTapped(object? sender, RoutedEventArgs e) =>
        Vm?.ApplySkinCommand.Execute(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape) return;
        // Esc из фулскрина сначала возвращает окно, потом уже шагает по режимам.
        if (WindowState == WindowState.FullScreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (Vm.HandleBack())
            e.Handled = true;
    }

    private WindowState _preFullscreenState = WindowState.Normal;
    private double _lastNormalWidth;
    private double _lastNormalHeight;

    /// <summary>Единая точка сохранения размера окна (Closing и Exit из App):
    /// компакт и фулскрин/максимайз не должны портить «нормальный» размер.</summary>
    public void PersistWindowSize()
    {
        if (Vm is null) return;
        var w = Width;
        var h = Height;
        if (WindowState != WindowState.Normal && _lastNormalWidth > 0)
        {
            w = _lastNormalWidth;
            h = _lastNormalHeight;
        }
        if (Vm.IsCompact)
        {
            h = Math.Max(_savedHeight, 520);
            if (_compactBy == "width") w = Math.Max(_savedWidth, 940);
        }
        Vm.RememberWindowSize(w, h);
    }

    /// <summary>F11/двойной клик по Обзору: окно на весь экран (видео и визуализатор).</summary>
    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullscreenState;
        }
        else
        {
            _preFullscreenState = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private void Overview_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Двойные клики по интерактиву (кнопки/списки/панель титров) не считаем:
        // даблклик по тексту — жест «выделить слово», а не «на весь экран».
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is null
                                 && v.FindAncestorOfType<ListBox>(includeSelf: true) is null
                                 && v.FindAncestorOfType<SpectrumControl>(includeSelf: true) is null
                                 && v.FindAncestorOfType<ScrollViewer>(includeSelf: true) is null)
            ToggleFullscreen();
    }

    /// <summary>
    /// Автопрокрутка к играющему треку: подматываем тот список, что сейчас на экране
    /// («Все треки» или очередь). Отложенная попытка — на случай, если контейнеры ещё не готовы.
    /// </summary>
    private void ScrollListsToPlaying()
    {
        if (Vm is null) return;
        if (QueueList.IsEffectivelyVisible && Vm.PlayingQueueIndex >= 0)
            QueueList.ScrollIntoView(Vm.PlayingQueueIndex);
        if (LibraryList.IsEffectivelyVisible && Vm.Library is { PlayingRowIndex: >= 0 } lib)
            LibraryList.ScrollIntoView(lib.PlayingRowIndex);
    }

    /// <summary>Караоке: текущая строка держится в центре панели титров.</summary>
    private void ScrollKaraokeToLine(int index) => ScrollKaraokeToLine(index, retry: true);

    private void ScrollKaraokeToLine(int index, bool retry)
    {
        var container = KaraokeItems.ContainerFromIndex(index);
        var ready = KaraokeScroll.IsEffectivelyVisible && KaraokeScroll.Viewport.Height > 0
                    && container is { Bounds.Height: > 0 };
        if (!ready)
        {
            // Сразу после подмены KaraokeLines контейнеров ещё нет (лейаут не прошёл) —
            // одна отложенная попытка после лейаута. Скрытая панель — просто не скроллим.
            if (retry) Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ScrollKaraokeToLine(index, retry: false),
                Avalonia.Threading.DispatcherPriority.Loaded);
            return;
        }
        var y = container!.Bounds.Y + container.Bounds.Height / 2;
        var target = Math.Max(0, y - KaraokeScroll.Viewport.Height / 2);
        KaraokeScroll.Offset = new Vector(KaraokeScroll.Offset.X, target);
    }

    private async Task<string?> PickPlaylistSaveAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            DefaultExtension = "m3u8",
            SuggestedFileName = "playlist.m3u8",
            FileTypeChoices = new[] { new FilePickerFileType("Playlist") { Patterns = new[] { "*.m3u8" } } },
        });
        return file?.TryGetLocalPath();
    }

    private void Jump_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Vm?.JumpCommand.Execute(null);
    }

    private void Library_DoubleTapped(object? sender, RoutedEventArgs e) => Vm?.Library?.PlayFromSelected();

    // Дерево: двойной клик по листу-треку играет его альбом с этого места (клик по узлам — просто раскрытие).
    private void Tree_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (LibraryTree.SelectedItem is ZBS.Library.LibraryTrack track)
            Vm?.Library?.PlayTreeTrack(track);
    }

    // M5: радио и подкасты.
    private void Radio_DoubleTapped(object? sender, RoutedEventArgs e) => Vm?.Radio.PlaySelectedCommand.Execute(null);

    private void RadioSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm?.Radio.SearchCommand.Execute(null);
    }

    private void Podcast_DoubleTapped(object? sender, RoutedEventArgs e) => Vm?.Podcasts.PlayEpisodeCommand.Execute(null);

    private void PodcastUrl_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm?.Podcasts.AddFeedCommand.Execute(null);
    }

    private void Queue_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: MainViewModel.QueueItem item })
            Vm?.PlayQueueItemCommand.Execute(item.Index);
    }

    private void BringToFront()
    {
        // В скин-режиме «показать плеер» = показать скин, главное окно остаётся спрятанным.
        if (_skinWindow is { } skin)
        {
            if (skin.WindowState == WindowState.Minimized)
                skin.WindowState = WindowState.Normal;
            skin.Activate();
            return;
        }
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private async Task<IReadOnlyList<string>> PickSkinFilesAsync()
    {
        var picks = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Скины") { Patterns = new[] { "*.wsz", "*.zbs" } },
            },
        });
        return picks
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
    }

    private async Task<string?> PickFolderAsync()
    {
        var picks = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
        });
        return picks.Count > 0 ? picks[0].TryGetLocalPath() : null;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is null) return;
        var paths = e.Data.GetFiles()?
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToArray();
        if (paths is { Length: > 0 })
            _ = Vm.OpenPathsAsync(paths); // все брошенные файлы/папки — один плейлист
    }

    private void Playlist_DoubleTapped(object? sender, RoutedEventArgs e) => Vm?.PlaySelected();
}
