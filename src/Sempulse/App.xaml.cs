using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Sempulse.Layout;
using Sempulse.Native;
using Sempulse.Pet;
using Sempulse.Services;
using Sempulse.Views;

namespace Sempulse;

public partial class App : Application, IPanelActions
{
    static Mutex? singleInstance;
    bool ownsInstance;

    readonly string dataDirectory = SettingsStore.DefaultDirectory;
    SettingsStore store = null!;
    PanelSettings settings = new();

    readonly SystemStats stats = new();
    readonly MediaService media = new();
    readonly WeatherService weather = new();
    readonly FullscreenMonitor fullscreen = new();
    readonly AppBar appBar = new();
    readonly DesktopPetHost pet = new();
    DesktopIconService desktopIcons = null!;

    PanelWindow panel = null!;
    PanelView? view;
    (PanelSettings Settings, PanelMode Mode)? viewKey;
    SettingsWindow? settingsWindow;

    bool isFullscreen;
    (int X, int Y)? savedWidget;
    int layoutGeneration;
    LayoutResult? lastLayout;

    WeatherInfo? lastWeather;
    (bool Busy, bool CanUndo, int? LastCount) iconState;
    readonly List<DispatcherTimer> timers = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        // "Sempulse.exe --quit" closes a running instance cleanly (used by scripts and installers).
        var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sempulse.Quit");
        singleInstance = new Mutex(true, @"Local\Sempulse.SingleInstance", out bool created);
        if (!created || e.Args.Contains("--quit"))
        {
            if (e.Args.Contains("--quit")) quitSignal.Set();
            Shutdown();
            return;
        }
        ownsInstance = true;
        new Thread(() => { quitSignal.WaitOne(); Dispatcher.BeginInvoke(Quit); }) { IsBackground = true }.Start();
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => appBar.Release();

        Directory.CreateDirectory(dataDirectory);
        store = new SettingsStore(dataDirectory);
        settings = store.Load();
        desktopIcons = new DesktopIconService(Path.Combine(dataDirectory, "desktop-icon-layout.txt"));

        panel = new PanelWindow();
        panel.DragFinished += () =>
        {
            var bounds = WindowPlacement.GetBounds(panel);
            if (!isFullscreen && settings.Mode == PanelMode.Widget) savedWidget = (bounds.X, bounds.Y);
        };
        panel.DpiChangedAfterMove += RestorePanelBounds;
        // Anything outside Sempulse that resizes the window (DPI or display changes) is undone.
        panel.SizeChanged += (_, _) => Dispatcher.BeginInvoke(RestorePanelBounds, DispatcherPriority.Background);
        panel.Show();
        ApplyLayout();

        fullscreen.Changed += value => Dispatcher.BeginInvoke(() => OnFullscreenChanged(value));
        fullscreen.Start();
        media.Changed += state => Dispatcher.BeginInvoke(() => view?.UpdateMedia(state));
        _ = media.StartAsync();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SyncPet();

        Every(TimeSpan.FromSeconds(1), () => view?.UpdateClock(DateTime.Now));
        Every(TimeSpan.FromSeconds(2), () => view?.UpdateStats(stats.Latest));
        Every(TimeSpan.FromMinutes(10), RefreshWeather);
        RefreshWeather();
        _ = RefreshIconStatus();
    }

    void Every(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) => tick();
        timer.Start();
        timers.Add(timer);
    }

    async void RefreshWeather()
    {
        lastWeather = await weather.GetAsync();
        view?.UpdateWeather(lastWeather);
    }

    void RestorePanelBounds()
    {
        if (lastLayout is not { Hidden: false } layout || !panel.IsVisible) return;
        var bounds = WindowPlacement.GetBounds(panel);
        bool wrongSize = bounds.Width != layout.Bounds.Width || bounds.Height != layout.Bounds.Height;
        bool wrongPlace = layout.EffectiveMode == PanelMode.Bar && (bounds.X != layout.Bounds.X || bounds.Y != layout.Bounds.Y);
        if (wrongSize || wrongPlace)
            WindowPlacement.SetBounds(panel, layout.EffectiveMode == PanelMode.Bar ? layout.Bounds : layout.Bounds with { X = bounds.X, Y = bounds.Y });
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyLayout);

    void OnFullscreenChanged(bool value)
    {
        if (isFullscreen == value) return;
        isFullscreen = value;
        settingsWindow?.Hide();
        ApplyLayout();
        SyncPet();
    }

    // ---- Layout ------------------------------------------------------------------------

    void ApplyLayout()
    {
        int generation = ++layoutGeneration;
        var (primary, secondary) = WindowPlacement.Displays();
        var layout = PanelLayout.Compute(settings, isFullscreen, primary, secondary, savedWidget);
        lastLayout = layout;
        if (layout.Hidden)
        {
            appBar.Release();
            panel.Hide();
            settingsWindow?.Hide();
            return;
        }

        var key = (settings with { PetMode = "" }, layout.EffectiveMode);
        if (viewKey != key)
        {
            view = new PanelView(settings, layout.EffectiveMode, this);
            viewKey = key;
            panel.Content = view;
            RefreshView();
        }
        panel.Draggable = layout.EffectiveMode == PanelMode.Widget;
        if (!panel.IsVisible) panel.Show();
        WindowPlacement.SetBounds(panel, layout.Bounds);

        if (layout.EffectiveMode == PanelMode.Bar)
        {
            int thickness = settings.Vertical ? layout.Bounds.Width : layout.Bounds.Height;
            appBar.Apply(settings.Position, thickness, primary.Bounds.X, primary.Bounds.Y, primary.Bounds.Width, primary.Bounds.Height);
            // Windows can shift the panel while it updates the work area.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (generation == layoutGeneration && lastLayout is { Hidden: false } current) WindowPlacement.SetBounds(panel, current.Bounds);
            };
            timer.Start();
        }
        else appBar.Release();

        if (settingsWindow is { IsVisible: true }) PositionSettings();
    }

    // A rebuilt view starts empty; fill it with the latest known values.
    void RefreshView()
    {
        if (view is null) return;
        view.UpdateClock(DateTime.Now);
        view.UpdateStats(stats.Latest);
        view.UpdateMedia(media.State);
        view.UpdateWeather(lastWeather);
        view.UpdateDesktopIcons(iconState.Busy, iconState.CanUndo, iconState.LastCount);
        view.UpdatePetMode(settings.PetMode);
    }

    void ChangeSettings(Func<PanelSettings, PanelSettings> update)
    {
        var previous = settings;
        settings = update(settings).Normalize();
        if (settings == previous) return;
        if (previous.Mode == PanelMode.Widget && settings.Mode == PanelMode.Bar) savedWidget = null;
        try { store.Save(settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Keeps working unsaved. */ }
        ApplyLayout();
        SyncPet();
        if (previous.Mode != settings.Mode) settingsWindow?.Rebuild();
    }

    // ---- Settings window --------------------------------------------------------------

    public void ToggleSettings()
    {
        if (settingsWindow is { IsVisible: true })
        {
            settingsWindow.Hide();
            return;
        }
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(() => settings, ChangeSettings, Quit);
            settingsWindow.SourceInitialized += (_, _) => WindowPlacement.MakeToolWindow(settingsWindow);
            settingsWindow.SizeChanged += (_, _) => { if (settingsWindow.IsVisible) PositionSettings(); };
        }
        settingsWindow.Rebuild();
        settingsWindow.Show();
        PositionSettings();
        settingsWindow.Activate();
    }

    void PositionSettings()
    {
        if (settingsWindow is null || lastLayout is not { Hidden: false } layout) return;
        var work = WindowPlacement.WorkAreaAt(layout.Bounds);
        double scale = WindowPlacement.ScaleAt(work.X + 1, work.Y + 1);
        var rect = PanelLayout.Settings(layout.Bounds, work, scale, layout.EffectiveMode, settings.Position,
            (int)Math.Ceiling(settingsWindow.ActualHeight));
        WindowPlacement.SetPosition(settingsWindow, rect.X, rect.Y);
    }

    void Quit()
    {
        Shutdown();
    }

    // ---- Media, desktop icons, cat -------------------------------------------------------

    public void MediaControl(string target, string action) => _ = media.ControlAsync(target, action);

    public void MoveDesktopIcons() => _ = RunDesktopIcons(desktopIcons.MoveAsync);
    public void UndoDesktopIcons() => _ = RunDesktopIcons(desktopIcons.UndoAsync);

    async Task RefreshIconStatus()
    {
        var result = await desktopIcons.StatusAsync();
        if (result.Ok) SetIconState(iconState with { CanUndo = result.CanUndo });
    }

    async Task RunDesktopIcons(Func<Task<DesktopIconResult>> action)
    {
        if (iconState.Busy) return;
        SetIconState(iconState with { Busy = true });
        DesktopIconResult result;
        try { result = await action(); }
        finally { SetIconState(iconState with { Busy = false }); }
        SetIconState(iconState with { CanUndo = result.CanUndo, LastCount = result.Ok ? result.Count : null });
        if (DesktopIconService.Messages.TryGetValue(result.Code, out string? message))
            MessageBox.Show(panel, message, "Sempulse", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void SetIconState((bool Busy, bool CanUndo, int? LastCount) state)
    {
        iconState = state;
        view?.UpdateDesktopIcons(state.Busy, state.CanUndo, state.LastCount);
    }

    public void SetPetMode(string mode) => ChangeSettings(s => s with { PetMode = mode });

    void SyncPet()
    {
        if (!settings.Pet)
        {
            pet.Stop();
            return;
        }
        if (!pet.IsRunning) pet.Start();
        pet.SetMode(settings.PetMode);
        pet.SetHidden(isFullscreen);
        view?.UpdatePetMode(settings.PetMode);
    }

    // ---- Shutdown ---------------------------------------------------------------------------

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { File.AppendAllText(Path.Combine(dataDirectory, "error.log"), $"{DateTime.Now:O} {e.Exception}\n\n"); }
        catch (Exception) { }
        e.Handled = true; // Keep the panel alive; the log tells what happened.
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!ownsInstance)
        {
            base.OnExit(e);
            return;
        }
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (var timer in timers) timer.Stop();
        appBar.Release();
        pet.Stop();
        fullscreen.Dispose();
        stats.Dispose();
        media.Dispose();
        weather.Dispose();
        singleInstance?.ReleaseMutex();
        base.OnExit(e);
    }
}
