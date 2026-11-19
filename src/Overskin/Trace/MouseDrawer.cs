using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using static Overskin.NativeMethods;

namespace Overskin;

public enum DrawOutcome { Completed, Aborted, Failed }

public readonly record struct DrawProgress(int CountdownSeconds, int PointsDone, int PointsTotal, TimeSpan Remaining, bool Paused = false)
{
    public bool IsCountdown => CountdownSeconds > 0;
    public double Fraction => PointsTotal == 0 ? 0 : (double)PointsDone / PointsTotal;
}

/// <summary>
/// One pen-down stroke, in physical screen pixels. When <see cref="ToolAt"/> is set, that spot
/// (the program's pen button) is clicked first unless it is already chosen; the same for
/// <see cref="SelectAt"/> (a palette swatch), <see cref="SizeAt"/> (a brush size) and
/// <see cref="OpacityAt"/>.
/// </summary>
public readonly record struct DrawStroke(Point[] Points, Point? SelectAt = null, Point? ToolAt = null,
    Point? SizeAt = null, Point? OpacityAt = null);

public sealed class DrawSettings
{
    /// <summary>Pause after every <see cref="PointsPerStep"/> points.</summary>
    public int DelayMs { get; init; } = 2;
    public int PointsPerStep { get; init; } = 1;
    public int CountdownSeconds { get; init; } = 3;
    /// <summary>Stop if the cursor is found somewhere it wasn't sent: the user grabbed the mouse.</summary>
    public bool StopOnMouseMove { get; init; } = true;
    /// <summary>Wait after clicking a palette swatch or tool button, so the program can switch.</summary>
    public int SelectPauseMs { get; init; } = 140;
    /// <summary>Glide to each stroke's start with the pen up, like a hand, instead of jumping there.</summary>
    public bool Humanize { get; init; }
    /// <summary>Pixels per tick at full speed when <see cref="Humanize"/> is on.</summary>
    public double HumanCruise { get; init; } = 16;
}

/// <summary>
/// Plays strokes back as real mouse input with SendInput, on its own thread.
/// The left button is always released when it stops, whatever the reason.
/// </summary>
public sealed class MouseDrawer
{
    private const int CornerSize = 4;      // px from the primary screen's top-left that count as the failsafe corner
    private const double MoveTolerance = 6; // px the cursor may differ from where we put it before we assume the user moved it

    private Thread? _thread;
    private volatile bool _abort;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _buttonDown;
    private string _abortReason = "Stopped.";

    /// <summary>Raised on the worker thread, at most ~20 times a second.</summary>
    public event Action<DrawProgress>? Progress;

    /// <summary>Raised on the worker thread once, after the button has been released.</summary>
    public event Action<DrawOutcome, string>? Finished;

    public bool IsRunning => _running;
    public bool IsPaused => _paused;

    /// <summary>Starts drawing. Points are physical screen pixels in virtual-desktop coordinates.</summary>
    public bool Start(IReadOnlyList<DrawStroke> strokes, DrawSettings settings)
    {
        if (_running) return false;
        _abort = false;
        _paused = false;
        _running = true;
        _abortReason = "Stopped.";

        _thread = new Thread(() => Run(strokes, settings))
        {
            IsBackground = true,
            Name = "Overskin mouse drawer",
        };
        _thread.Start();
        return true;
    }

    /// <summary>Asks the worker to stop. Safe to call from any thread, any time.</summary>
    public void Abort(string reason = "Stopped.")
    {
        if (!_running) return;
        _abortReason = reason;
        _abort = true;
    }

    /// <summary>Pauses or resumes. The button is lifted while paused and put back down on resume.</summary>
    public void TogglePause()
    {
        if (_running) _paused = !_paused;
    }

    /// <summary>For exit paths: stop, wait briefly, and make sure the button is up no matter what.</summary>
    public void Shutdown()
    {
        bool wasRunning = _running;
        Abort();
        var thread = _thread;
        if (thread != null && thread.IsAlive) thread.Join(600);
        if (wasRunning || _buttonDown) ReleaseButton();
    }

    /// <summary>
    /// Wait around each pen lift. Presses and releases are never merged by a browser the way
    /// moves are, so this can be shorter than the per-point delay.
    /// </summary>
    private static int Settle(int delayMs) => Math.Clamp(delayMs / 2, 2, 10);

    /// <summary>Rough duration for the UI, before anything has been timed.</summary>
    public static TimeSpan Estimate(int strokes, int points, int colourSwitches, DrawSettings s)
    {
        double perPoint = s.DelayMs / (double)Math.Max(1, s.PointsPerStep) + 0.05;
        double perStroke = 4 * Settle(s.DelayMs) + 0.5;
        double perSwitch = 3 * Settle(s.DelayMs) + s.SelectPauseMs;
        return TimeSpan.FromMilliseconds(points * perPoint + strokes * perStroke + colourSwitches * perSwitch);
    }

    private void Run(IReadOnlyList<DrawStroke> strokes, DrawSettings s)
    {
        var outcome = DrawOutcome.Completed;
        string message = "Done.";
        bool highRes = false;
        int delay = Math.Max(0, s.DelayMs), perStep = Math.Max(1, s.PointsPerStep), settle = Settle(delay);

        int total = 0, done = 0;
        foreach (var st in strokes) total += st.Points.Length;
        var clock = new Stopwatch();
        long lastReport = -1000;
        Point? lastSent = null;

        void Report(bool force = false)
        {
            long now = clock.ElapsedMilliseconds;
            if (!force && now - lastReport < 50) return;
            lastReport = now;
            double rate = done / Math.Max(1.0, now);
            var remaining = TimeSpan.FromMilliseconds((total - done) / Math.Max(rate, 1e-6));
            Progress?.Invoke(new DrawProgress(0, done, total, remaining));
        }

        // Stop checks, plus pause handling: lift the pen, wait, then put it back where it was.
        void Check()
        {
            if (ShouldStop(lastSent, s.StopOnMouseMove)) throw new OperationCanceledException();
            if (!_paused) return;

            bool wasDown = _buttonDown;
            if (wasDown) ReleaseButton();
            clock.Stop();
            Progress?.Invoke(new DrawProgress(0, done, total, TimeSpan.Zero, Paused: true));
            while (_paused)
            {
                if (ShouldStop(null, false)) throw new OperationCanceledException();
                Thread.Sleep(30);
            }
            clock.Start();

            if (lastSent is { } back)
            {
                Move(back);
                Thread.Sleep(settle);
                if (wasDown)
                {
                    PressButton();
                    Thread.Sleep(settle);
                }
            }
            Report(force: true);
        }

        void Move(Point p)
        {
            if (!MoveTo(p))
                throw new InvalidOperationException(
                    "Windows blocked the mouse input. The program you're drawing into is probably running as administrator; run Overskin as administrator too.");
            lastSent = p;
        }

        try
        {
            // countdown, in short slices so an abort is noticed straight away
            for (int sec = s.CountdownSeconds; sec > 0; sec--)
            {
                Progress?.Invoke(new DrawProgress(sec, 0, total, TimeSpan.Zero));
                var slice = Stopwatch.StartNew();
                while (slice.ElapsedMilliseconds < 1000)
                {
                    if (ShouldStop(null, false)) throw new OperationCanceledException();
                    Thread.Sleep(25);
                }
            }

            // default timer granularity is ~15 ms, far too coarse for per-point delays
            highRes = timeBeginPeriod(1) == 0;
            clock.Start();
            Point? selected = null, tool = null, size = null, opacity = null;
            int sinceDelay = 0;

            void Click(Point at, int pauseMs)
            {
                Move(at);
                Thread.Sleep(settle);
                PressButton();
                Thread.Sleep(settle);
                ReleaseButton();
                Thread.Sleep(pauseMs);
                Check();
            }

            foreach (var stroke in strokes)
            {
                var path = stroke.Points;
                if (path.Length == 0) continue;
                Check();

                // switch tool and colour first, if this stroke needs different ones
                if (stroke.ToolAt is { } button && button != tool)
                {
                    Click(button, s.SelectPauseMs);
                    tool = button;
                }
                if (stroke.SizeAt is { } brush && brush != size)
                {
                    Click(brush, s.SelectPauseMs);
                    size = brush;
                }
                if (stroke.OpacityAt is { } level && level != opacity)
                {
                    Click(level, s.SelectPauseMs);
                    opacity = level;
                }
                if (stroke.SelectAt is { } swatch && swatch != selected)
                {
                    Click(swatch, s.SelectPauseMs);
                    selected = swatch;
                }

                if (s.Humanize && lastSent is { } from)
                {
                    // pen up: glide over to the next stroke
                    foreach (var p in HumanStroke.Travel(from, path[0], s.HumanCruise))
                    {
                        Move(p);
                        if (delay > 0) Thread.Sleep(delay);
                        Check();
                    }
                }
                Move(path[0]);
                Thread.Sleep(settle);
                PressButton();
                Thread.Sleep(settle);

                for (int i = 1; i < path.Length; i++)
                {
                    Move(path[i]);
                    done++;

                    if (++sinceDelay >= perStep)
                    {
                        sinceDelay = 0;
                        if (delay > 0) Thread.Sleep(delay);
                        Check();
                        Report();
                    }
                }
                done++;

                // a single dot still needs the program to see the button held for a moment
                Thread.Sleep(settle);
                ReleaseButton();
                Thread.Sleep(settle);
                Check();
                Report();
            }

            Progress?.Invoke(new DrawProgress(0, total, total, TimeSpan.Zero));
        }
        catch (OperationCanceledException)
        {
            outcome = DrawOutcome.Aborted;
            message = _abortReason;
        }
        catch (Exception ex)
        {
            outcome = DrawOutcome.Failed;
            message = ex is InvalidOperationException ? ex.Message : "Drawing failed: " + ex.Message;
        }
        finally
        {
            ReleaseButton(); // always, even if we believe it is already up
            if (highRes) timeEndPeriod(1);
            _paused = false;
            _running = false;
        }

        Finished?.Invoke(outcome, message);
    }

    /// <summary>Checks every way the user can stop a drawing.</summary>
    private bool ShouldStop(Point? lastSent, bool stopOnMove)
    {
        if (_abort) return true;

        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
        {
            _abortReason = "Stopped with Esc.";
            return true;
        }

        var pos = CursorPos();

        // Failsafe: cursor shoved into the top-left corner of the primary screen.
        // Ignored when the drawing itself legitimately passes through that corner.
        if (pos.X >= 0 && pos.X <= CornerSize && pos.Y >= 0 && pos.Y <= CornerSize)
        {
            bool ours = lastSent is { } p && p.X <= CornerSize + 2 && p.Y <= CornerSize + 2 && p.X >= -2 && p.Y >= -2;
            if (!ours)
            {
                _abortReason = "Stopped: mouse moved to the top-left corner.";
                return true;
            }
        }

        // The cursor is not where we put it: someone is moving the real mouse.
        if (stopOnMove && lastSent is { } sent)
        {
            double dx = pos.X - sent.X, dy = pos.Y - sent.Y;
            if (dx * dx + dy * dy > MoveTolerance * MoveTolerance)
            {
                _abortReason = "Stopped because the mouse was moved.";
                return true;
            }
        }
        return false;
    }

    // ---- input ---------------------------------------------------------------------------

    /// <summary>Moves the cursor to a physical screen pixel. False if Windows refused the input.</summary>
    internal static bool MoveTo(Point screen)
    {
        // Absolute coordinates are normalised to 0..65535 across the whole virtual desktop,
        // which is what makes this line up on multi-monitor and scaled setups.
        var vs = VirtualScreen();
        double nx = (screen.X - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1);
        double ny = (screen.Y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1);

        return Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            (int)Math.Round(Math.Clamp(nx, 0, 65535)),
            (int)Math.Round(Math.Clamp(ny, 0, 65535)));
    }

    private void PressButton()
    {
        _buttonDown = true;
        Send(MOUSEEVENTF_LEFTDOWN, 0, 0);
    }

    private void ReleaseButton()
    {
        Send(MOUSEEVENTF_LEFTUP, 0, 0);
        _buttonDown = false;
    }

    /// <summary>Presses or releases the left button where the cursor is.</summary>
    internal static void SendLeftButton(bool down) => Send(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0, 0);

    /// <summary>SendInput returns 0 when the input was blocked (for example by an elevated foreground window).</summary>
    private static bool Send(uint flags, int dx, int dy)
    {
        var input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].u.mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags };
        return SendInput(1, input, Marshal.SizeOf<INPUT>()) == 1;
    }
}
