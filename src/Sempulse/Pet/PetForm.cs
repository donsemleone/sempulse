#nullable disable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Sempulse.Pet;

// A small pixel-art cat that walks along the edges of every monitor's work area, climbs
// its walls and ceiling, travels between monitors, jumps onto window tops and chases the
// mouse pointer. DesktopPetHost runs it on its own UI thread and forwards
// "show", "hide" and "mode <name>" commands.
sealed class PetForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Width, Height; public SIZE(int w, int h) { Width = w; Height = h; } }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT dstPoint, ref SIZE size,
        IntPtr src, ref POINT srcPoint, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    const int GWL_EXSTYLE = -20;
    const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    // Sprite canvas in art pixels. Frames are bottom-aligned; the top rows hold sleep "z" marks.
    const int ArtWidth = 32, ArtHeight = 34;
    const double Gravity = 2600;

    enum Mode { Walk, Sit, Sleep, Fall, Drag, Crouch, Puff, Chase }
    // Which side of the work area the feet touch. Walls and ceiling draw rotated sprites.
    enum Edge { Floor, RightWall, Ceiling, LeftWall }

    class Monitor { public RECT Work; public int Scale; }

    readonly uint ownProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    readonly Random random = new Random();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Dictionary<string, IntPtr> bitmaps = new Dictionary<string, IntPtr>();
    List<Monitor> monitors = new List<Monitor>();

    Mode mode = Mode.Sit;
    string forcedMode = "auto"; // Chosen from the panel.
    Edge edge = Edge.Floor;
    // Anchor: the point where the feet touch the surface (centre of the body along it).
    double anchorX, anchorY, speedX, speedY;
    int direction = 1, scale = 3, zPhase;
    IntPtr surface = IntPtr.Zero; // Window the cat stands on; zero means a work-area edge.
    int modeUntil, nextBlink, blinkUntil, yawnUntil, lastTick, lastStep, lastMonitorRefresh, lastTopmost;
    bool walkFrame, hidden, dragMoved, pendingSleep;
    // A trip is a long walk along the edges to a spot on some monitor's floor.
    bool tripping;
    RECT tripGoal;
    double tripGoalX;
    int tripUntil;
    // Pointer play: a chase session, a watching pose when out of reach, and pounces.
    Point lastCursor;
    int lastCursorMove, playUntil, nextPlayCheck, nextPounce;
    bool chaseIdle, pouncing;
    double pounceX, pounceY;
    Point dragOffset, dragStart;
    string presentedKey;
    int presentedX = int.MinValue, presentedY = int.MinValue;

    public PetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Hand;
        Text = "Sempulse Cat";
        timer.Tick += delegate { Step(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_MOUSEACTIVATE) { message.Result = new IntPtr(MA_NOACTIVATE); return; }
        base.WndProc(ref message);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshMonitors();
        Monitor start = monitors[random.Next(monitors.Count)];
        scale = start.Scale;
        anchorX = start.Work.Left + (start.Work.Right - start.Work.Left) * (0.25 + random.NextDouble() * 0.5);
        anchorY = start.Work.Bottom;
        lastTick = lastStep = Environment.TickCount;
        EnterMode(Mode.Sit, 2000, 4000);
        Present();
        timer.Start();
    }

    public void HandleCommand(string command)
    {
        if (command == "hide" && !hidden)
        {
            hidden = true;
            timer.Stop();
            ShowWindow(Handle, 0);
        }
        else if (command == "show" && hidden)
        {
            hidden = false;
            lastTick = lastStep = Environment.TickCount;
            presentedKey = null;
            ShowWindow(Handle, 4);
            Present();
            timer.Start();
        }
        else if (command.StartsWith("mode "))
        {
            string name = command.Substring(5);
            if (name != "walk" && name != "sit" && name != "sleep" && name != "jump" && name != "puff" && name != "play") name = "auto";
            forcedMode = name;
            if (name != "auto" && name != "walk") tripping = false;
            playUntil = 0;
            if (name != "auto" && mode != Mode.Drag && mode != Mode.Fall) ChooseNextMode();
        }
        else if (command == "exit")
        {
            Close();
        }
    }

    // ---- Behaviour -------------------------------------------------------

    int HalfLength { get { return ArtWidth * scale / 2; } }
    int Thickness { get { return ArtHeight * scale; } }
    static bool LateNight { get { int hour = DateTime.Now.Hour; return hour >= 23 || hour < 6; } }
    bool Playing { get { return forcedMode == "play" || (forcedMode == "auto" && Environment.TickCount < playUntil); } }
    bool Tripping { get { return tripping && Environment.TickCount < tripUntil; } }

    void EnterMode(Mode next, int minMs, int maxMs)
    {
        mode = next;
        modeUntil = Environment.TickCount + random.Next(minMs, maxMs);
        if (next == Mode.Walk && edge == Edge.Floor) direction = random.Next(2) == 0 ? -1 : 1;
        if (next == Mode.Sit) nextBlink = Environment.TickCount + random.Next(1200, 3500);
        zPhase = 0;
        timer.Interval = next == Mode.Sleep ? 600 : next == Mode.Sit || next == Mode.Puff ? 150 : next == Mode.Walk || next == Mode.Chase ? 50 : 30;
    }

    void ChooseNextMode()
    {
        int now = Environment.TickCount;
        bool waking = mode == Mode.Sleep;
        bool yawned = pendingSleep;
        pendingSleep = false;
        string next = forcedMode;
        if (Playing) next = "chase";
        else if (yawned && (next == "auto" || next == "sleep")) next = "sleep";
        else if (Tripping && (next == "auto" || next == "walk")) next = "trip";
        else if (next == "jump") next = mode == Mode.Walk ? "jump" : "walk";
        else if (next == "auto")
        {
            double roll = random.NextDouble();
            // Late at night the cat is lazier: fewer walks, more naps.
            if (waking) next = "sit";
            else if (LateNight) next = roll < 0.22 ? "walk" : roll < 0.5 ? "sit" : roll < 0.86 ? "sleep" : roll < 0.94 ? "jump" : "puff";
            else next = roll < 0.4 ? "walk" : roll < 0.68 ? "sit" : roll < 0.8 ? "sleep" : roll < 0.92 ? "jump" : "puff";
        }
        if (next == "walk" && (forcedMode == "auto" || forcedMode == "walk") &&
            random.NextDouble() < (forcedMode == "walk" ? 0.4 : LateNight ? 0.1 : 0.3)) next = "trip";

        // A sleepy cat yawns before lying down.
        if (next == "sleep" && !yawned && LateNight && mode == Mode.Sit && edge == Edge.Floor)
        {
            EnterMode(Mode.Sit, 1700, 1701);
            yawnUntil = now + 1500;
            pendingSleep = true;
            return;
        }

        // Only walking happens on walls and the ceiling; anything else starts with a drop.
        if (edge != Edge.Floor && next != "walk" && next != "trip") { LetGo(); return; }

        switch (next)
        {
            case "walk": EnterMode(Mode.Walk, forcedMode == "jump" ? 1500 : 3000, forcedMode == "jump" ? 3500 : 10000); break;
            case "trip":
                if (!Tripping) StartTrip();
                EnterMode(Mode.Walk, 180000, 180001);
                direction = TripDirection(direction);
                break;
            case "chase": EnterMode(Mode.Chase, 3000, 6000); chaseIdle = false; break;
            case "sleep": EnterMode(Mode.Sleep, 20000, 60000); break;
            case "jump": EnterMode(Mode.Crouch, 280, 281); break;
            case "puff": EnterMode(Mode.Puff, 3000, 6000); break;
            default: EnterMode(Mode.Sit, 3000, 9000); break;
        }
        if (waking && mode == Mode.Sit && LateNight) yawnUntil = now + 1500;
    }

    // ---- Trips between monitors ------------------------------------------

    // Picks a spot on some monitor's floor, usually on a monitor other than the current one.
    void StartTrip()
    {
        Monitor here = Here();
        List<Monitor> choices = new List<Monitor>();
        foreach (Monitor monitor in monitors) if (monitor != here) choices.Add(monitor);
        if (choices.Count == 0 || (here != null && random.NextDouble() < 0.25)) choices = new List<Monitor> { here ?? monitors[0] };
        Monitor goal = choices[random.Next(choices.Count)];
        int width = goal.Work.Right - goal.Work.Left;
        double margin = Math.Min(3 * HalfLength, width / 2.0);
        tripGoal = goal.Work;
        tripGoalX = goal.Work.Left + margin + random.NextDouble() * Math.Max(0, width - 2 * margin);
        tripUntil = Environment.TickCount + 180000;
        tripping = true;
    }

    bool IsTripGoal(Monitor monitor)
    {
        return monitor != null && monitor.Work.Left == tripGoal.Left && monitor.Work.Top == tripGoal.Top &&
            monitor.Work.Right == tripGoal.Right && monitor.Work.Bottom == tripGoal.Bottom;
    }

    // The monitor whose edge the feet are on.
    Monitor Here()
    {
        switch (edge)
        {
            case Edge.RightWall: return MonitorAt(anchorX - 1, anchorY);
            case Edge.LeftWall: return MonitorAt(anchorX + 1, anchorY);
            case Edge.Ceiling: return MonitorAt(anchorX, anchorY + 1);
            default: return MonitorAt(anchorX, anchorY - 1);
        }
    }

    // -1 when the trip goal lies above this monitor, 1 below, 0 beside it.
    int GoalVertical(Monitor here)
    {
        return tripGoal.Bottom <= here.Work.Top ? -1 : tripGoal.Top >= here.Work.Bottom ? 1 : 0;
    }

    // True when this monitor's wall runs straight on into a monitor above (vertical -1) or below (1).
    bool WallContinues(Monitor here, bool right, int vertical)
    {
        int wallX = right ? here.Work.Right : here.Work.Left;
        Monitor next = MonitorAt(right ? wallX - 1 : wallX, vertical < 0 ? here.Work.Top - 1 : here.Work.Bottom);
        return next != null && (right ? next.Work.Right : next.Work.Left) == wallX;
    }

    // Direction toward the trip goal. On walls it only overrides the natural direction when the
    // wall runs on toward the goal, so the cat never bounces back to the edge it just left.
    int TripDirection(int natural)
    {
        Monitor here = Here();
        if (!Tripping || here == null) return natural;
        if (edge == Edge.RightWall || edge == Edge.LeftWall)
        {
            if (IsTripGoal(here)) return natural;
            int vertical = GoalVertical(here);
            return vertical != 0 && WallContinues(here, edge == Edge.RightWall, vertical) ? vertical : natural;
        }
        if (edge == Edge.Floor && IsTripGoal(here)) return tripGoalX < anchorX ? -1 : 1;
        int goalVertical = GoalVertical(here);
        if (goalVertical != 0)
        {
            bool left = WallContinues(here, false, goalVertical), right = WallContinues(here, true, goalVertical);
            if (left != right) return left ? -1 : 1;
            if (left) return anchorX - here.Work.Left < here.Work.Right - anchorX ? -1 : 1;
        }
        return (tripGoal.Left + tripGoal.Right) / 2.0 < anchorX ? -1 : 1;
    }

    void Step()
    {
        int now = Environment.TickCount;
        double seconds = Math.Min(0.1, Math.Max(0, (now - lastTick) / 1000.0));
        lastTick = now;
        if (now - lastMonitorRefresh > 2000) RefreshMonitors();
        if (now - lastTopmost > 3000)
        {
            lastTopmost = now;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        if (mode != Mode.Drag && mode != Mode.Fall && !HasSupport()) LetGo();

        Point cursor = Cursor.Position;
        if (cursor != lastCursor) { lastCursor = cursor; lastCursorMove = now; }
        // In auto mode a pointer moving close by sometimes starts a short play session.
        if (forcedMode == "auto" && !Playing && !Tripping && edge == Edge.Floor && (mode == Mode.Sit || mode == Mode.Walk) &&
            now >= nextPlayCheck)
        {
            nextPlayCheck = now + 1000;
            double rise = anchorY - cursor.Y;
            if (now - lastCursorMove < 400 && Math.Abs(cursor.X - anchorX) < 80 * scale && rise > -10 * scale &&
                rise < 120 * scale && random.NextDouble() < 0.3)
            {
                playUntil = now + random.Next(8000, 16000);
                ChooseNextMode();
            }
        }

        switch (mode)
        {
            case Mode.Walk: Walk(seconds, now); break;
            case Mode.Chase: Chase(seconds, now, cursor); break;
            case Mode.Fall: Fly(seconds); break;
            case Mode.Sit:
                if (now >= nextBlink)
                {
                    // Late at night some blinks turn into yawns.
                    if (LateNight && random.NextDouble() < 0.3) { yawnUntil = now + 1500; nextBlink = now + random.Next(4000, 8000); }
                    else { blinkUntil = now + 160; nextBlink = now + random.Next(2000, 5000); }
                }
                break;
            case Mode.Sleep: zPhase = (zPhase + 1) % 4; break;
            case Mode.Puff: if (now - lastStep > 260) { walkFrame = !walkFrame; lastStep = now; } break;
            case Mode.Crouch: if (now >= modeUntil) Leap(); break;
        }
        if (mode != Mode.Drag && mode != Mode.Fall && mode != Mode.Crouch && now >= modeUntil) ChooseNextMode();
        Present();
    }

    void Walk(double seconds, int now)
    {
        bool trip = Tripping;
        if (tripping && !trip) { tripping = false; modeUntil = now; } // Gave up on a goal it could not reach.
        // On a trip the cat trots: quicker steps and a faster pace.
        if (now - lastStep > (trip ? 130 : 220)) { walkFrame = !walkFrame; lastStep = now; }
        double distance = (trip ? 26 : 11) * scale * seconds;
        if (trip && edge == Edge.Floor && IsTripGoal(Here()))
        {
            if (Math.Abs(tripGoalX - anchorX) < 8 * scale) { tripping = false; modeUntil = now; return; }
            direction = tripGoalX < anchorX ? -1 : 1;
        }
        Edge before = edge;
        if (edge == Edge.Floor) WalkFloor(distance);
        else if (edge == Edge.Ceiling) WalkCeiling(distance, seconds);
        else WalkWall(distance, seconds);
        if (trip && edge != before && mode == Mode.Walk) direction = TripDirection(direction);
    }

    // Runs after the pointer, watches it when out of reach and pounces when it hovers just above.
    void Chase(double seconds, int now, Point cursor)
    {
        if (forcedMode == "auto" && (!Playing || now - lastCursorMove > 4000)) { playUntil = 0; modeUntil = now; return; }
        if (forcedMode == "play") modeUntil = now + 1000;
        double dx = cursor.X - anchorX, rise = anchorY - cursor.Y;
        if (chaseIdle && now >= nextBlink) { blinkUntil = now + 160; nextBlink = now + random.Next(2000, 5000); }

        if (now >= nextPounce && now - lastCursorMove < 1500 && rise > 12 * scale && rise < 110 * scale && Math.Abs(dx) < 45 * scale)
        {
            pouncing = true;
            pounceX = cursor.X;
            pounceY = cursor.Y + 6 * scale;
            nextPounce = now + random.Next(1500, 3500);
            direction = dx < 0 ? -1 : 1;
            EnterMode(Mode.Crouch, 220, 260);
            return;
        }
        if (Math.Abs(dx) < 8 * scale) { chaseIdle = true; return; }

        direction = dx < 0 ? -1 : 1;
        if (now - lastStep > 110) { walkFrame = !walkFrame; lastStep = now; }
        double nextX = anchorX + direction * Math.Min(Math.Abs(dx), 30 * scale * seconds);
        chaseIdle = false;
        if (surface != IntPtr.Zero)
        {
            RECT rect;
            if (!SurfaceRect(surface, out rect)) { LetGo(); return; }
            if (nextX < rect.Left || nextX > rect.Right)
            {
                // Jumps down after a pointer below the window, otherwise waits at its edge.
                if (cursor.Y > rect.Top) { anchorX = nextX; LetGo(); }
                else chaseIdle = true;
                return;
            }
            anchorX = nextX;
            return;
        }
        Monitor current = MonitorAt(anchorX, anchorY - 1);
        Monitor ahead = MonitorAt(nextX + direction * HalfLength, anchorY - 1);
        if (current != null && (ahead == null || ahead.Work.Bottom < anchorY - 1)) { chaseIdle = true; return; }
        anchorX = nextX;
        if (ahead != null && ahead.Work.Bottom > anchorY + 1 && MonitorAt(anchorX, anchorY - 1) == ahead) LetGo();
    }

    void WalkFloor(double distance)
    {
        double nextX = anchorX + direction * distance;
        if (surface != IntPtr.Zero)
        {
            RECT rect;
            if (!SurfaceRect(surface, out rect)) { LetGo(); return; }
            anchorY = rect.Top;
            if (nextX < rect.Left || nextX > rect.Right)
            {
                // At a window edge the cat either turns back or steps off and falls; travellers step off.
                if (!Tripping && random.Next(2) == 0) { direction = -direction; return; }
                anchorX = nextX;
                LetGo();
                return;
            }
            anchorX = nextX;
            return;
        }

        Monitor current = MonitorAt(anchorX, anchorY - 1);
        double front = nextX + direction * HalfLength;
        Monitor ahead = MonitorAt(front, anchorY - 1);
        if (current != null && (ahead == null || ahead.Work.Bottom < anchorY - 1))
        {
            // Reached a wall: climb it most of the time, otherwise turn around. Travellers always climb.
            if (Tripping || random.NextDouble() < 0.65)
            {
                edge = direction > 0 ? Edge.RightWall : Edge.LeftWall;
                anchorX = direction > 0 ? current.Work.Right : current.Work.Left;
                anchorY = current.Work.Bottom - HalfLength;
                direction = -1;
            }
            else direction = -direction;
            return;
        }
        anchorX = nextX;
        if (ahead != null && ahead.Work.Bottom > anchorY + 1 && MonitorAt(anchorX, anchorY - 1) == ahead) LetGo();
    }

    void WalkWall(double distance, double seconds)
    {
        bool right = edge == Edge.RightWall;
        Monitor current = MonitorAt(right ? anchorX - 1 : anchorX + 1, anchorY);
        if (current == null) { LetGo(); return; }
        if (!Tripping && random.NextDouble() < 0.08 * seconds) direction = -direction;
        anchorY += direction * distance;
        int wallX = right ? current.Work.Right : current.Work.Left;

        if (direction < 0 && anchorY - HalfLength <= current.Work.Top)
        {
            Monitor above = MonitorAt(right ? wallX - 1 : wallX + 1, current.Work.Top - 1);
            if (above != null && (right ? above.Work.Right : above.Work.Left) == wallX) return;
            edge = Edge.Ceiling;
            anchorX = right ? wallX - HalfLength : wallX + HalfLength;
            anchorY = current.Work.Top;
            direction = right ? -1 : 1;
        }
        else if (direction > 0 && anchorY + HalfLength >= current.Work.Bottom)
        {
            Monitor below = MonitorAt(right ? wallX - 1 : wallX + 1, current.Work.Bottom + 1);
            if (below != null && (right ? below.Work.Right : below.Work.Left) == wallX) return;
            edge = Edge.Floor;
            anchorX = right ? wallX - HalfLength : wallX + HalfLength;
            anchorY = current.Work.Bottom;
            direction = right ? -1 : 1;
        }
    }

    void WalkCeiling(double distance, double seconds)
    {
        Monitor current = MonitorAt(anchorX, anchorY + 1);
        if (current == null || (!Tripping && random.NextDouble() < 0.05 * seconds)) { LetGo(); return; }
        anchorX += direction * distance;

        if (direction < 0 && anchorX - HalfLength <= current.Work.Left)
        {
            Monitor beside = MonitorAt(current.Work.Left - 1, anchorY + 1);
            if (beside != null && beside.Work.Top == current.Work.Top) return;
            edge = Edge.LeftWall;
            anchorX = current.Work.Left;
            anchorY = current.Work.Top + HalfLength;
            direction = 1;
        }
        else if (direction > 0 && anchorX + HalfLength >= current.Work.Right)
        {
            Monitor beside = MonitorAt(current.Work.Right + 1, anchorY + 1);
            if (beside != null && beside.Work.Top == current.Work.Top) return;
            edge = Edge.RightWall;
            anchorX = current.Work.Right;
            anchorY = current.Work.Top + HalfLength;
            direction = 1;
        }
    }

    // Drops from wherever the cat is. Anchors switch to the feet of an upright falling cat.
    void LetGo()
    {
        if (edge == Edge.Ceiling) anchorY += Thickness;
        else if (edge == Edge.RightWall) { anchorX -= Thickness / 2; anchorY += HalfLength; }
        else if (edge == Edge.LeftWall) { anchorX += Thickness / 2; anchorY += HalfLength; }
        edge = Edge.Floor;
        surface = IntPtr.Zero;
        speedX = 0;
        speedY = 0;
        EnterMode(Mode.Fall, 0, 1);
    }

    // Leaves the crouch: pounces at the pointer, aims for a visible window top within reach,
    // or otherwise hops forward.
    void Leap()
    {
        double targetX = pounceX, targetY = pounceY;
        IntPtr target;
        surface = IntPtr.Zero;
        bool pounce = pouncing;
        pouncing = false;
        if (pounce || FindJumpTarget(out targetX, out targetY, out target))
        {
            double rise = anchorY - targetY;
            double peak = Math.Max(0, rise) + 50;
            speedY = -Math.Sqrt(2 * Gravity * peak);
            double flight = -speedY / Gravity + Math.Sqrt(2 * (peak - rise) / Gravity);
            speedX = (targetX - anchorX) / flight;
            direction = speedX < 0 ? -1 : 1;
        }
        else
        {
            speedY = -Math.Sqrt(2 * Gravity * (18 * scale));
            speedX = direction * 70 * scale;
        }
        mode = Mode.Fall;
        timer.Interval = 30;
    }

    void Fly(double seconds)
    {
        double previousY = anchorY;
        speedY = Math.Min(speedY + Gravity * seconds, 2400);
        anchorY += speedY * seconds;
        anchorX += speedX * seconds;

        Monitor nearest = NearestMonitor(anchorX);
        if (anchorX < nearest.Work.Left + HalfLength || anchorX > nearest.Work.Right - HalfLength)
        {
            anchorX = Clamp(anchorX, nearest.Work.Left + HalfLength, nearest.Work.Right - HalfLength);
            speedX = 0;
        }
        if (speedY <= 0) return;

        IntPtr window;
        double landing;
        if (FindSurface(anchorX, previousY, anchorY, out landing, out window))
        {
            anchorY = landing;
            surface = window;
            speedX = 0;
            Land();
            return;
        }
        if (anchorY > LowestFloor())
        {
            anchorX = Clamp(anchorX, nearest.Work.Left + 40, nearest.Work.Right - 40);
            anchorY = nearest.Work.Bottom;
            surface = IntPtr.Zero;
            Land();
        }
    }

    // A playing or travelling cat only pauses briefly after landing.
    void Land()
    {
        if (Playing) EnterMode(Mode.Sit, 250, 600);
        else if (Tripping) EnterMode(Mode.Sit, 400, 900);
        else EnterMode(Mode.Sit, 900, 2500);
    }

    bool HasSupport()
    {
        if (edge != Edge.Floor)
        {
            Monitor wall = MonitorAt(edge == Edge.RightWall ? anchorX - 1 : edge == Edge.LeftWall ? anchorX + 1 : anchorX,
                edge == Edge.Ceiling ? anchorY + 1 : anchorY);
            if (wall == null) return false;
            if (edge == Edge.RightWall) anchorX = wall.Work.Right;
            else if (edge == Edge.LeftWall) anchorX = wall.Work.Left;
            else anchorY = wall.Work.Top;
            return true;
        }
        if (surface != IntPtr.Zero)
        {
            RECT rect;
            if (!SurfaceRect(surface, out rect) || anchorX < rect.Left || anchorX > rect.Right) { surface = IntPtr.Zero; return false; }
            anchorY = rect.Top;
            return true;
        }
        Monitor monitor = MonitorAt(anchorX, anchorY - 1);
        if (monitor == null) return false;
        if (Math.Abs(monitor.Work.Bottom - anchorY) <= 1) return true;
        if (monitor.Work.Bottom < anchorY) { anchorY = monitor.Work.Bottom; return true; } // Taskbar or panel grew.
        return false;
    }

    // ---- Mouse -----------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (edge != Edge.Floor) LetGo();
        tripping = false;
        pouncing = false;
        Point cursor = Cursor.Position;
        dragStart = cursor;
        dragOffset = new Point(cursor.X - (int)Math.Round(anchorX), cursor.Y - (int)Math.Round(anchorY));
        dragMoved = false;
        mode = Mode.Drag;
        surface = IntPtr.Zero;
        timer.Interval = 30;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (mode != Mode.Drag || e.Button != MouseButtons.Left) return;
        Point cursor = Cursor.Position;
        if (Math.Abs(cursor.X - dragStart.X) + Math.Abs(cursor.Y - dragStart.Y) > 4) dragMoved = true;
        anchorX = cursor.X - dragOffset.X;
        anchorY = cursor.Y - dragOffset.Y;
        Present();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (mode != Mode.Drag) return;
        speedX = 0;
        speedY = 0;
        if (dragMoved) { EnterMode(Mode.Fall, 0, 1); return; }
        EnterMode(Mode.Sit, 3000, 6000); // A click wakes the cat or makes it stop and look.
        blinkUntil = Environment.TickCount + 220;
    }

    // ---- Surfaces --------------------------------------------------------

    bool SurfaceRect(IntPtr hwnd, out RECT rect)
    {
        rect = new RECT();
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        int cloaked;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(RECT))) != 0 &&
            !GetWindowRect(hwnd, out rect)) return false;
        return true;
    }

    // Visits candidate window tops from front to back, skipping tops hidden behind a higher window at x.
    void VisitWindowTops(double x, Func<IntPtr, RECT, bool> visit)
    {
        List<RECT> above = new List<RECT>();
        EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
        {
            RECT rect;
            if (!CandidateWindow(hwnd, out rect)) return true;
            bool covered = false;
            foreach (RECT higher in above)
            {
                if (x >= higher.Left && x < higher.Right && rect.Top >= higher.Top && rect.Top < higher.Bottom) { covered = true; break; }
            }
            above.Add(rect);
            Monitor monitor = MonitorAt(x, rect.Top + 1);
            // Maximized windows have no top edge worth standing on.
            if (covered || monitor == null || rect.Top <= monitor.Work.Top + Thickness) return true;
            return visit(hwnd, rect);
        }, IntPtr.Zero);
    }

    // Finds the highest window top or work-area floor crossed between two feet positions.
    bool FindSurface(double x, double fromY, double toY, out double landing, out IntPtr window)
    {
        double bestY = double.MaxValue;
        IntPtr bestWindow = IntPtr.Zero;
        foreach (Monitor monitor in monitors)
        {
            if (x >= monitor.Work.Left && x < monitor.Work.Right && monitor.Work.Bottom >= fromY - 1 &&
                monitor.Work.Bottom <= toY && monitor.Work.Bottom < bestY) bestY = monitor.Work.Bottom;
        }
        VisitWindowTops(x, delegate(IntPtr hwnd, RECT rect)
        {
            if (x >= rect.Left + 8 && x <= rect.Right - 8 && rect.Top >= fromY - 1 && rect.Top <= toY && rect.Top < bestY)
            {
                bestY = rect.Top;
                bestWindow = hwnd;
            }
            return true;
        });
        landing = bestY;
        window = bestWindow;
        return bestY != double.MaxValue;
    }

    bool FindJumpTarget(out double targetX, out double targetY, out IntPtr target)
    {
        double reach = 140 * scale, height = 150 * scale;
        List<double[]> spots = new List<double[]>();
        List<IntPtr> windows = new List<IntPtr>();
        foreach (int side in new int[] { -1, 1 })
        {
            double probeX = anchorX + side * reach * 0.5;
            VisitWindowTops(probeX, delegate(IntPtr hwnd, RECT rect)
            {
                double rise = anchorY - rect.Top;
                if (rise < 30 || rise > height) return true;
                double left = rect.Left + HalfLength, right = rect.Right - HalfLength;
                if (right <= left) return true;
                double x = Clamp(anchorX + side * 40 * scale, left, right);
                if (Math.Abs(x - anchorX) > reach) return true;
                spots.Add(new double[] { x, rect.Top });
                windows.Add(hwnd);
                return true;
            });
        }
        if (spots.Count == 0) { targetX = targetY = 0; target = IntPtr.Zero; return false; }
        int pick = random.Next(spots.Count);
        targetX = spots[pick][0];
        targetY = spots[pick][1];
        target = windows[pick];
        return true;
    }

    bool CandidateWindow(IntPtr hwnd, out RECT rect)
    {
        rect = new RECT();
        if (hwnd == Handle || !SurfaceRect(hwnd, out rect)) return false;
        if (rect.Right - rect.Left < 150 || rect.Bottom - rect.Top < 40) return false;
        uint processId;
        GetWindowThreadProcessId(hwnd, out processId);
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TRANSPARENT) != 0) return false;
        // Sempulse's own windows are tool windows, but they are fine places to sit.
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && processId != ownProcessId) return false;
        StringBuilder className = new StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        string name = className.ToString();
        return name != "Progman" && name != "WorkerW" && name != "Shell_TrayWnd" && name != "Shell_SecondaryTrayWnd";
    }

    // ---- Monitors --------------------------------------------------------

    void RefreshMonitors()
    {
        lastMonitorRefresh = Environment.TickCount;
        List<Monitor> found = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref RECT bounds, IntPtr data)
        {
            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(handle, ref info)) return true;
            uint dpiX = 96, dpiY = 96;
            try { GetDpiForMonitor(handle, 0, out dpiX, out dpiY); }
            catch (Exception) { dpiX = 96; }
            Monitor monitor = new Monitor();
            monitor.Work = info.rcWork;
            monitor.Scale = Math.Max(2, (int)Math.Round(3 * dpiX / 96.0));
            found.Add(monitor);
            return true;
        }, IntPtr.Zero);
        if (found.Count > 0) monitors = found;
        if (mode != Mode.Drag && mode != Mode.Fall && edge == Edge.Floor &&
            MonitorAt(anchorX, anchorY - 1) == null && MonitorAt(anchorX, anchorY - 40) == null)
        {
            Monitor home = NearestMonitor(anchorX);
            anchorX = Clamp(anchorX, home.Work.Left + 40, home.Work.Right - 40);
            anchorY = home.Work.Bottom;
            surface = IntPtr.Zero;
        }
    }

    Monitor MonitorAt(double x, double y)
    {
        foreach (Monitor monitor in monitors)
        {
            if (x >= monitor.Work.Left && x < monitor.Work.Right && y >= monitor.Work.Top && y < monitor.Work.Bottom) return monitor;
        }
        return null;
    }

    Monitor PrimaryMonitor()
    {
        foreach (Monitor monitor in monitors)
        {
            if (monitor.Work.Left <= 0 && monitor.Work.Right > 0 && monitor.Work.Top <= 0 && monitor.Work.Bottom > 0) return monitor;
        }
        return monitors[0];
    }

    Monitor NearestMonitor(double x)
    {
        Monitor best = monitors[0];
        double bestDistance = double.MaxValue;
        foreach (Monitor monitor in monitors)
        {
            double distance = x < monitor.Work.Left ? monitor.Work.Left - x : x >= monitor.Work.Right ? x - monitor.Work.Right : 0;
            if (distance < bestDistance) { bestDistance = distance; best = monitor; }
        }
        return best;
    }

    double LowestFloor()
    {
        double lowest = double.MinValue;
        foreach (Monitor monitor in monitors) lowest = Math.Max(lowest, monitor.Work.Bottom);
        return lowest;
    }

    static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }

    // ---- Drawing ---------------------------------------------------------

    void Present()
    {
        if (hidden) return;
        Monitor monitor = MonitorAt(anchorX, anchorY - 1);
        if (monitor != null && mode != Mode.Drag && edge == Edge.Floor) scale = monitor.Scale;

        string[] frame;
        string name;
        bool flip = direction < 0;
        int zMarks = 0;
        switch (mode)
        {
            case Mode.Walk: frame = walkFrame ? WalkA : WalkB; name = walkFrame ? "wa" : "wb"; break;
            case Mode.Crouch: frame = WalkB; name = "wb"; break;
            case Mode.Fall: frame = Jump; name = "ju"; flip = speedX != 0 ? speedX < 0 : direction < 0; break;
            case Mode.Puff: frame = walkFrame ? PuffA : PuffB; name = walkFrame ? "pa" : "pb"; break;
            case Mode.Sleep: frame = Sleep; name = "sl"; zMarks = zPhase; break;
            case Mode.Chase:
                if (!chaseIdle) { frame = walkFrame ? WalkA : WalkB; name = walkFrame ? "wa" : "wb"; break; }
                goto default; // Watching the pointer while sitting.
            default:
                int tick = Environment.TickCount;
                bool yawn = tick < yawnUntil, blink = !yawn && tick < blinkUntil;
                frame = yawn ? Yawn : blink ? Blink : Sit;
                name = yawn ? "yw" : blink ? "bl" : "si";
                flip = false;
                break;
        }
        // Rotation per edge: walls turn the sprite a quarter, the ceiling turns it upside down.
        int rotation = edge == Edge.Floor ? 0 : edge == Edge.RightWall ? 1 : edge == Edge.Ceiling ? 2 : 3;
        if (edge == Edge.RightWall) flip = direction > 0;
        else if (edge == Edge.Ceiling) flip = direction > 0;
        else if (edge == Edge.LeftWall) flip = direction < 0;

        string key = name + (flip ? "-f" : "") + "-r" + rotation + "-z" + zMarks + "-s" + scale;
        int length = ArtWidth * scale, thickness = ArtHeight * scale;
        int width = rotation % 2 == 0 ? length : thickness, height = rotation % 2 == 0 ? thickness : length;
        int ax = (int)Math.Round(anchorX), ay = (int)Math.Round(anchorY);
        int x, y;
        switch (edge)
        {
            case Edge.RightWall: x = ax - thickness; y = ay - length / 2; break;
            case Edge.Ceiling: x = ax - length / 2; y = ay; break;
            case Edge.LeftWall: x = ax; y = ay - length / 2; break;
            default: x = ax - length / 2; y = ay - thickness; break;
        }
        if (key == presentedKey && x == presentedX && y == presentedY) return;

        IntPtr bitmap;
        if (!bitmaps.TryGetValue(key, out bitmap))
        {
            bitmap = RenderFrame(frame, flip, rotation, zMarks, scale);
            bitmaps[key] = bitmap;
        }

        if (key == presentedKey)
        {
            SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
        }
        else
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr previous = SelectObject(memory, bitmap);
            POINT destination = new POINT(x, y), source = new POINT(0, 0);
            SIZE size = new SIZE(width, height);
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1; // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
            SelectObject(memory, previous);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
        presentedKey = key;
        presentedX = x;
        presentedY = y;
    }

    // rotation: 0 upright, 1 quarter turn counter-clockwise (feet right), 2 upside down, 3 clockwise (feet left).
    static IntPtr RenderFrame(string[] frame, bool flip, int rotation, int zMarks, int scale)
    {
        char[,] art = new char[ArtWidth, ArtHeight];
        int top = ArtHeight - frame.Length;
        for (int row = 0; row < frame.Length; row++)
            for (int column = 0; column < ArtWidth && column < frame[row].Length; column++)
                art[flip ? ArtWidth - 1 - column : column, top + row] = frame[row][column];

        // Sleep marks rise from the head: one, two or three "z" glyphs.
        int[,] zSpots = { { 21, 16 }, { 24, 10 }, { 27, 4 } };
        for (int mark = 0; mark < zMarks; mark++)
            for (int row = 0; row < ZGlyph.Length; row++)
                for (int column = 0; column < ZGlyph[row].Length; column++)
                    if (ZGlyph[row][column] == '#')
                    {
                        int zx = zSpots[mark, 0] + column;
                        art[flip ? ArtWidth - 1 - zx : zx, zSpots[mark, 1] + row] = 'O';
                    }

        int outWidth = rotation % 2 == 0 ? ArtWidth : ArtHeight, outHeight = rotation % 2 == 0 ? ArtHeight : ArtWidth;
        using (Bitmap image = new Bitmap(outWidth * scale, outHeight * scale, PixelFormat.Format32bppArgb))
        {
            BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int[] pixels = new int[image.Width * image.Height];
            for (int artY = 0; artY < ArtHeight; artY++)
            {
                for (int artX = 0; artX < ArtWidth; artX++)
                {
                    int color = ColorOf(art[artX, artY]);
                    if (color == 0) continue;
                    int outX, outY;
                    switch (rotation)
                    {
                        case 1: outX = artY; outY = ArtWidth - 1 - artX; break;
                        case 2: outX = ArtWidth - 1 - artX; outY = ArtHeight - 1 - artY; break;
                        case 3: outX = ArtHeight - 1 - artY; outY = artX; break;
                        default: outX = artX; outY = artY; break;
                    }
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            pixels[(outY * scale + dy) * image.Width + outX * scale + dx] = color;
                }
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            image.UnlockBits(data);
            return image.GetHbitmap(Color.FromArgb(0));
        }
    }

    static int ColorOf(char code)
    {
        switch (code)
        {
            case 'Y': return unchecked((int)0xFFFFD23F);
            case 'O': return unchecked((int)0xFFE89B1C);
            case 'L': return unchecked((int)0xFFFFF3B0);
            case 'P': return unchecked((int)0xFFFF7B9C);
            case 'K': return unchecked((int)0xFF3A2414);
            case 'W': return unchecked((int)0xFFFFFFFF);
            case 'R': return unchecked((int)0xFF8B2E3C);
            default: return 0;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        foreach (IntPtr bitmap in bitmaps.Values) DeleteObject(bitmap);
        base.OnFormClosed(e);
    }

    static readonly string[] ZGlyph = { "#####", "...#.", "..#..", ".#...", "#####" };

    static readonly string[] WalkA = {
        "....................K........K..",
        "....................KK......KK..",
        ".KK.................KPK....KPK..",
        "KYOK...............KYPPKKKKPPYK.",
        "KYYK...............KYYYYOOYYYYK.",
        ".KOYK.............KYYYYYYYYYYYYK",
        "..KYK.............KYYYYKKYYYKKYK",
        "..KYKKKKKKKKKKKKKKKYYYYWKYYYWKYK",
        "...KYYOYYYOYYYOYYYKYYYYKKYYYKKYK",
        "...KYYOYYYOYYYOYYYKYYYYYYLPLLYYK",
        "...KYYYYYYYYYYYYYYYKYYYYYLKLLYK.",
        "...KYYYYYYYYYYYYYYYYKKKKKKKKKK..",
        "...KYYYYYYYYYYYYYYYYYLLLK.......",
        "....KYYYYYYYYYYYYYYYLLLK........",
        "....KYYYYKKKKKKKKYYYYYKK........",
        "....KYK..KYK....KYK..KYK........",
        "....KYK..KYK....KYK..KYK........",
        "...KYK....KYK..KYK....KYK.......",
        "...KKK....KKK..KKK....KKK.......",
    };
    static readonly string[] WalkB = {
        "....................K........K..",
        "....................KK......KK..",
        ".KK.................KPK....KPK..",
        "KYOK...............KYPPKKKKPPYK.",
        "KYYK...............KYYYYOOYYYYK.",
        ".KOYK.............KYYYYYYYYYYYYK",
        "..KYK.............KYYYYKKYYYKKYK",
        "..KYKKKKKKKKKKKKKKKYYYYWKYYYWKYK",
        "...KYYOYYYOYYYOYYYKYYYYKKYYYKKYK",
        "...KYYOYYYOYYYOYYYKYYYYYYLPLLYYK",
        "...KYYYYYYYYYYYYYYYKYYYYYLKLLYK.",
        "...KYYYYYYYYYYYYYYYYKKKKKKKKKK..",
        "...KYYYYYYYYYYYYYYYYYLLLK.......",
        "....KYYYYYYYYYYYYYYYLLLK........",
        "....KYYYYKKKKKKKKYYYYYKK........",
        ".....KYKKYK......KYKKYK.........",
        ".....KYKKYK......KYKKYK.........",
        ".....KYKKYK......KYKKYK.........",
        ".....KKKKKK......KKKKKK.........",
    };
    static readonly string[] Sit = {
        ".........K............K.........",
        ".........KK..........KK.........",
        ".........KPK........KPK.........",
        "........KYPPK......KPPYK........",
        "........KYPPYKKKKKKYPPYK........",
        "........KYYYYOYOOYOYYYYK........",
        "........KYYYYYYOOYYYYYYK........",
        ".......KYYYYYYYYYYYYYYYYK.......",
        ".......KYKKYYYYYYYYYYKKYK.......",
        ".......KYWKYYYYYYYYYYWKYK.......",
        ".......KYKKYYYYYYYYYYKKYK.......",
        "...KKKKYYYYYLLLPPLLLYYYYYKKKK...",
        ".......KYYYLLLLKKLLLLYYYK.......",
        "....KKKKYYYYLKLLLLKLYYYYKKKK....",
        "........KKYYYLLLLLLYYYKK........",
        "..........KKYLLLLLLYKK..KK......",
        "..........KYYLLLLLLYYK..YOK.....",
        ".........KYOYLLLLLLYOYK.OYK.....",
        ".........KYYOLLLLLLOYYK.YOK.....",
        ".........KYOYYLLLLYYOYK.OYK.....",
        "........KYYYYYLLLLYYYYYKYOK.....",
        "........KYYKYYYKKYYYKYYKOYK.....",
        "........KYYKYYYKKYYYKYYKYOK.....",
        "........KYYKYYYKKYYYKYYKOYK.....",
        "........KYYKLYLKKLYLKYYKYYK.....",
        "........KKKKKKKKKKKKKKKKKK......",
    };
    static readonly string[] Blink = {
        ".........K............K.........",
        ".........KK..........KK.........",
        ".........KPK........KPK.........",
        "........KYPPK......KPPYK........",
        "........KYPPYKKKKKKYPPYK........",
        "........KYYYYOYOOYOYYYYK........",
        "........KYYYYYYOOYYYYYYK........",
        ".......KYYYYYYYYYYYYYYYYK.......",
        ".......KYYYYYYYYYYYYYYYYK.......",
        ".......KYYYYYYYYYYYYYYYYK.......",
        ".......KYKKYYYYYYYYYYKKYK.......",
        "...KKKKYYYYYLLLPPLLLYYYYYKKKK...",
        ".......KYYYLLLLKKLLLLYYYK.......",
        "....KKKKYYYYLKLLLLKLYYYYKKKK....",
        "........KKYYYLLLLLLYYYKK........",
        "..........KKYLLLLLLYKK..KK......",
        "..........KYYLLLLLLYYK..YOK.....",
        ".........KYOYLLLLLLYOYK.OYK.....",
        ".........KYYOLLLLLLOYYK.YOK.....",
        ".........KYOYYLLLLYYOYK.OYK.....",
        "........KYYYYYLLLLYYYYYKYOK.....",
        "........KYYKYYYKKYYYKYYKOYK.....",
        "........KYYKYYYKKYYYKYYKYOK.....",
        "........KYYKYYYKKYYYKYYKOYK.....",
        "........KYYKLYLKKLYLKYYKYYK.....",
        "........KKKKKKKKKKKKKKKKKK......",
    };
    // Late-night yawn: eyes squeezed shut, mouth wide open with the tongue showing.
    static readonly string[] Yawn = {
        ".........K............K.........",
        ".........KK..........KK.........",
        ".........KPK........KPK.........",
        "........KYPPK......KPPYK........",
        "........KYPPYKKKKKKYPPYK........",
        "........KYYYYOYOOYOYYYYK........",
        "........KYYYYYYOOYYYYYYK........",
        ".......KYYYYYYYYYYYYYYYYK.......",
        ".......KYYYYYYYYYYYYYYYYK.......",
        ".......KYKYYYYYYYYYYYYKYK.......",
        ".......KYYKKYYYYYYYYKKYYK.......",
        "...KKKKYYYYYLLLPPLLLYYYYYKKKK...",
        ".......KYYYLLLKKKKLLLYYYK.......",
        "....KKKKYYYYLKRRRRKLYYYYKKKK....",
        "........KKYYYKRPPRKYYYKK........",
        "..........KKYLKKKKLYKK..KK......",
        "..........KYYLLLLLLYYK..YOK.....",
        ".........KYOYLLLLLLYOYK.OYK.....",
        ".........KYYOLLLLLLOYYK.YOK.....",
        ".........KYOYYLLLLYYOYK.OYK.....",
        "........KYYYYYLLLLYYYYYKYOK.....",
        "........KYYKYYYKKYYYKYYKOYK.....",
        "........KYYKYYYKKYYYKYYKYOK.....",
        "........KYYKYYYKKYYYKYYKOYK.....",
        "........KYYKLYLKKLYLKYYKYYK.....",
        "........KKKKKKKKKKKKKKKKKK......",
    };
    static readonly string[] Sleep = {
        ".................K............K.",
        ".................KK..........KK.",
        ".....KKKKKKKKKKKKKPK........KPK.",
        "...KKYYOYYYOYYYOYKPPKKKKKKKKPPK.",
        "..KYYYOYYYOYYYOYKYPPYYOYYOYYPPYK",
        ".KYYYYYYYYYYYYYYKYYYYYYYYYYYYYYK",
        ".KYYYYYYYYYYYYYYKYKYYKYYYYKYYKYK",
        ".KYYYYYYYYYYYYYYKYYKKYYYYYYKKYYK",
        ".KYKKKKKKKKKKKYYKYYYYYLPPLYYYYYK",
        "KYOYOYOYOYOYOKLLKYYYYLLKKLLYYYYK",
        ".KKKKKKKKKKKKKLLKKYYYLLLLLLYYYK.",
        "..KKKKKKKKKKKKKKKKKKKKKKKKKKKK..",
    };
    static readonly string[] Jump = {
        "....................K........K..",
        "....................KK......KK..",
        "....................KPK....KPK..",
        "...................KYPPKKKKPPYK.",
        "...................KYYYYOOYYYYK.",
        "..................KYYYYYYYYYYYYK",
        "KKK...............KYYYYKKYYYKKYK",
        "KYOYKKKKKKKKKKKKKKKYYYYWKYYYWKYK",
        "KKKOYYOYYYOYYYOYYYKYYYYKKYYYKKYK",
        "...KYYOYYYOYYYOYYYKYYYYYYLPLLYYK",
        "...KYYYYYYYYYYYYYYYKYYYYYLKLLYK.",
        "...KYYYYYYYYYYYYYYYYKKKKKKKKKK..",
        "...KYYYYYYYYYYYYYYYYYLLLK.......",
        "....KYYYYYYYYYYYYYYYLLLK........",
        "....KYYYYKKKKKKKKYYYYYKK........",
        "....KYK..KYK....KYK..KYK........",
        "...KYK..KYK......KYK..KYK.......",
        "..KYK..KYK........KYK..KYK......",
        ".KKK..KKK..........KKK..KKK.....",
    };
    static readonly string[] PuffA = {
        "..KKK...........................",
        "KKYYYKK.........................",
        ".KYOYK..........................",
        "KYYYYYK.........................",
        ".KYYYK......K...................",
        "KYYYYYK.K.K.K.K.K...............",
        ".KYOYK..K.KKKKK.K...............",
        "KYYYYYK.KKYYYOYKK...............",
        ".KYYYKKKYOYYYOYYYKK.............",
        "KYYYYYKYYOYYYYYYYOK.K........K..",
        ".KYOYKYYYYYYYYYYYOYKKK......KK..",
        "KYYYYYKYYYYYYYYYYYYYKPK....KPK..",
        ".KYYYKYYYYKKKKKYYYYKYPPKKKKPPYK.",
        "KYYYYYKYKYK....KYKYKYYYYOOYYYYK.",
        "...KYYYKKYK....KYKKYYYYYYYYYYYYK",
        "...KYYK.KYK....KYKKYYYYKKYYYKKYK",
        "...KKYK.KYK....KYKKYYYYWKYYYWKYK",
        "...KKYK.KYK....KYKKYYYYKKYYYKKYK",
        "...KKYK.KYK....KYKKYYYYYYPKLLYYK",
        "....KYK.KYK....KYKYKYYYYYLKLLYK.",
        "....KYK.KYK....KYK.KYK..........",
        "....KYK.KYK....KYK.KYK..........",
        "....KYK.KYK....KYK.KYK..........",
        "....KKK.KKK....KKK.KKK..........",
    };
    static readonly string[] PuffB = {
        "..KKK...........................",
        "KKYYYKK.........................",
        ".KYOYK..........................",
        "KYYYYYK.........................",
        ".KYYYK......K...................",
        "KYYYYYK.K..K.K.KK...............",
        ".KYOYK...KKKKKK..K..............",
        "KYYYYYK.KKYYYOYKK...............",
        ".KYYYK.KYOYYYOYYYK.K............",
        "KYYYYYKYYOYYYYYYYOK.K........K..",
        ".KYO.KYYYYYYYYYYYOYK.K......KK..",
        "KYYYYYKYYYYYYYYYYYYYKPK....KPK..",
        ".KYYYKYYYYKKKKKYYYYKYPPKKKKPPYK.",
        "KYYYYYKYKYK....KYKYKYYYYOOYYYYK.",
        "...KYYYKKYK....KYKKYYYYYYYYYYYYK",
        "...KYYK.KYK....KYKKYYYYKKYYYKKYK",
        "...KKYK.KYK....KYKKYYYYWKYYYWKYK",
        "...KKYK.KYK....KYKKYYYYKKYYYKKYK",
        "...KKYK.KYK....KYKKYYYYYYPKLLYYK",
        "....KYK.KYK....KYKYKYYYYYLKLLYK.",
        "....KYK.KYK....KYK.KYK..........",
        "....KYK.KYK....KYK.KYK..........",
        "....KYK.KYK....KYK.KYK..........",
        "....KKK.KKK....KKK.KKK..........",
    };
}
