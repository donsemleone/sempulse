using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace Sempulse.Services;

public sealed record MediaInfo(string Title, string Artist, bool IsPlaying, bool Exists);

public sealed record MediaState(MediaInfo Spotify, MediaInfo Other, MediaInfo Active)
{
    public static readonly MediaState Empty = new(
        new("Spotify Kapalı", "", false, false),
        new("Medya Yok", "", false, false),
        new("Medya Yok", "", false, false));
}

public sealed record MediaSessionInfo(string AppId, string Title, string Artist, bool IsPlaying);

// Windows media sessions (GSMTC): what is playing and play/pause/next/previous per target.
public sealed class MediaService : IDisposable
{
    GlobalSystemMediaTransportControlsSessionManager? manager;
    readonly List<GlobalSystemMediaTransportControlsSession> watched = [];
    readonly SemaphoreSlim refreshLock = new(1, 1);
    int refreshPending;

    public event Action<MediaState>? Changed;
    public MediaState State { get; private set; } = MediaState.Empty;

    public async Task StartAsync()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.SessionsChanged += (_, _) => QueueRefresh();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Media sessions unavailable: {exception.Message}");
        }
    }

    void QueueRefresh()
    {
        // Several events usually fire together; collapse them into one refresh.
        if (Interlocked.Exchange(ref refreshPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            Interlocked.Exchange(ref refreshPending, 0);
            await RefreshAsync();
        });
    }

    async Task RefreshAsync()
    {
        if (manager is null) return;
        await refreshLock.WaitAsync();
        try
        {
            var sessions = manager.GetSessions();
            foreach (var session in watched)
            {
                session.MediaPropertiesChanged -= OnSessionChanged;
                session.PlaybackInfoChanged -= OnSessionChanged;
            }
            watched.Clear();
            var infos = new List<MediaSessionInfo>();
            foreach (var session in sessions)
            {
                session.MediaPropertiesChanged += OnSessionChanged;
                session.PlaybackInfoChanged += OnSessionChanged;
                watched.Add(session);
                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync();
                    var playback = session.GetPlaybackInfo();
                    infos.Add(new MediaSessionInfo(
                        session.SourceAppUserModelId ?? "",
                        properties?.Title ?? "",
                        properties?.Artist ?? "",
                        playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
                }
                catch (Exception) { /* A session can close while it is being read. */ }
            }
            State = BuildState(infos);
            Changed?.Invoke(State);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Media refresh failed: {exception.Message}");
        }
        finally { refreshLock.Release(); }
    }

    void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => QueueRefresh();

    static bool IsSpotify(MediaSessionInfo session) => session.AppId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    // Same grouping the panel always used: Spotify, the playing (or any) non-Spotify session, and the active one.
    public static MediaState BuildState(IReadOnlyList<MediaSessionInfo> sessions)
    {
        var spotify = sessions.FirstOrDefault(session => session.AppId.Length > 0 && IsSpotify(session));
        var other = sessions.FirstOrDefault(session => session.AppId.Length > 0 && !IsSpotify(session) && session.IsPlaying)
            ?? sessions.FirstOrDefault(session => session.AppId.Length > 0 && !IsSpotify(session));
        var active = sessions.FirstOrDefault(session => session.IsPlaying) ?? sessions.FirstOrDefault();
        return new MediaState(
            spotify is null ? MediaState.Empty.Spotify : new(Or(spotify.Title, "Spotify"), spotify.Artist, spotify.IsPlaying, true),
            other is null ? MediaState.Empty.Other : new(Or(other.Title, "Medya"), other.Artist, other.IsPlaying, true),
            active is null ? MediaState.Empty.Active : new(Or(active.Title, "Medya Yok"), active.Artist, active.IsPlaying, true));
    }

    static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    // ---- Controls ----------------------------------------------------------

    public async Task ControlAsync(string target, string action)
    {
        try
        {
            if (manager is null) throw new InvalidOperationException("No media session manager");
            var sessions = manager.GetSessions();
            GlobalSystemMediaTransportControlsSession? session = target switch
            {
                "spotify" => sessions.FirstOrDefault(s => s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase)),
                "other" => sessions.FirstOrDefault(s => !s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase)
                               && s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                           ?? sessions.FirstOrDefault(s => !s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase)),
                _ => manager.GetCurrentSession(),
            };
            if (session is null)
            {
                Fallback(target, action);
                return;
            }
            _ = action switch
            {
                "next" => await session.TrySkipNextAsync(),
                "prev" => await session.TrySkipPreviousAsync(),
                _ => await session.TryTogglePlayPauseAsync(),
            };
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Media control failed: {exception.Message}");
            Fallback(target, action);
        }
    }

    // Without a usable session: media keys for the global target, WM_APPCOMMAND for browser players.
    static void Fallback(string target, string action)
    {
        if (target == "global") SendMediaKey(action);
        else if (target == "other") SendAppCommandToBrowsers(action);
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    const uint KEYEVENTF_EXTENDEDKEY = 0x1;
    const uint WM_APPCOMMAND = 0x0319;

    static void SendMediaKey(string action)
    {
        byte key = action switch { "next" => 0xB0, "prev" => 0xB1, _ => 0xB3 };
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
    }

    static readonly string[] Browsers = ["chrome", "msedge", "firefox", "opera", "brave", "vivaldi"];

    static void SendAppCommandToBrowsers(string action)
    {
        int command = action switch { "next" => 11, "prev" => 12, _ => 14 };
        var lParam = (IntPtr)(command << 16);
        var browserIds = new HashSet<uint>();
        foreach (string name in Browsers)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                browserIds.Add((uint)process.Id);
                process.Dispose();
            }
        }
        if (browserIds.Count == 0) return;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (browserIds.Contains(processId)) PostMessage(hwnd, WM_APPCOMMAND, hwnd, lParam);
            return true;
        }, IntPtr.Zero);
    }

    public void Dispose()
    {
        foreach (var session in watched)
        {
            session.MediaPropertiesChanged -= OnSessionChanged;
            session.PlaybackInfoChanged -= OnSessionChanged;
        }
        watched.Clear();
    }
}
