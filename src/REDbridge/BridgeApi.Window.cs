using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using StreamJsonRpc;

namespace REDbridge;

/// <summary>
/// The game in an editor tab (bridge 0.20.0, issue #22, for REDcode's Game view; design:
/// https://edgemodders.github.io/REDcode/design/game-view.html). The game stays its own window;
/// <c>window.dock</c> keeps it on a webview of VS Code's window: owned by that window, its frame
/// taken off, placed (never resized) at the webview's corner, clipped to it with a window region,
/// hidden with an empty region while the webview is not shown, with a release key that frees the
/// pointer. Windows only, user32 and UI Automation (through COM, so no WPF runtime is needed).
///
/// Off unless REDbridge was started with <c>--windows</c>: REDnode's host lends its REDbridge to
/// packages and in-game servers with no method filter, and these methods move other programs' windows.
/// One window is docked at a time. Notifications: <c>window.released</c>, <c>window.lost</c>,
/// <c>window.focus</c>, <c>window.shown</c>.
/// </summary>
public sealed partial class BridgeApi
{
    public sealed record WinRect(int X, int Y, int Width, int Height);

    /// <summary>A top-level window: its handle, process, class, title, rectangle, client size and style.</summary>
    public sealed record WindowFound(long Hwnd, int Pid, string Class, string Title, WinRect Rect, int ClientWidth, int ClientHeight,
        string Style, string ExStyle, bool Visible, bool Minimized, bool Foreground, long Owner);

    /// <summary>The window to dock onto: by handle, or by process (its own or one of its parents) and a part of its title.</summary>
    public sealed record OwnerSpec(int? Pid = null, string? Title = null, long? Hwnd = null);

    /// <summary>What <c>window.state</c> answers.</summary>
    public sealed record WindowDockState(bool Docked, string? Class, long Hwnd, long Owner, string? Webview, WinRect? WebviewRect,
        WinRect? WindowRect, bool Shown, bool Focused, string? ReleaseKey, bool ReleaseKeyRegistered, string? Ended);

    private WindowDocker? _docker;
    private readonly object _dockLock = new();
    private static int _dpiSet;

    private void RequireWindows()
    {
        if (!_host.WindowsEnabled)
        {
            throw new InvalidOperationException("REDbridge's window methods are off: they answer only a REDbridge started with --windows, as REDcode starts it for its Game view");
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The window methods are for Windows only");
        }
        // Rectangles in real pixels, as UI Automation gives the webview's when the process is DPI aware.
        if (Interlocked.Exchange(ref _dpiSet, 1) == 0 && !Win.SetProcessDpiAwarenessContext(Win.PerMonitorAwareV2))
        {
            Win.SetProcessDPIAware();
        }
    }

    /// <summary>
    /// The top-level window of a class, such as the game's <c>W2ViewportClass</c>, or null. Found by
    /// class, since an owned window is no longer its process's main window. <paramref name="pid"/>
    /// picks one process's when several have the class.
    /// </summary>
    [JsonRpcMethod("window.find")]
    public WindowFound? WindowFind(string @class, int? pid = null)
    {
        RequireWindows();
        var hwnd = Win.FindTop(@class, pid);
        return hwnd == IntPtr.Zero ? null : Win.Describe(hwnd);
    }

    /// <summary>
    /// Starts keeping a window of <paramref name="class"/> on a webview of the <paramref name="owner"/>
    /// window: the UI Automation Document element whose name is <paramref name="webview"/> (the page's
    /// <c>&lt;title&gt;</c>). <paramref name="releaseKey"/> (Ctrl+Alt+Backspace by default, or e.g.
    /// "Pause") frees the pointer and gives the focus to the owner. A window already docked is undocked first.
    /// </summary>
    [JsonRpcMethod("window.dock")]
    public WindowDockState WindowDock(string @class, OwnerSpec owner, string webview, string? releaseKey = null, int? pid = null)
    {
        RequireWindows();
        if (string.IsNullOrWhiteSpace(webview))
        {
            throw new ArgumentException("webview is the page's title, as UI Automation names its Document element");
        }
        var game = Win.FindTop(@class, pid);
        if (game == IntPtr.Zero)
        {
            throw new InvalidOperationException($"No top-level window of class '{@class}'{(pid is null ? "" : $" in process {pid}")}");
        }
        var ownerHwnd = Win.FindOwner(owner ?? new OwnerSpec());
        if (ownerHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"No window found for owner {System.Text.Json.JsonSerializer.Serialize(owner)}");
        }
        if (ownerHwnd == game)
        {
            throw new ArgumentException("The owner window is the window to dock");
        }
        var key = HotKey.Parse(releaseKey ?? "Ctrl+Alt+Backspace");
        WindowDocker docker;
        lock (_dockLock)
        {
            _docker?.Stop();
            docker = _docker = new WindowDocker(_host, @class, game, ownerHwnd, webview, key);
        }
        docker.Start();
        docker.WaitFirstTick(TimeSpan.FromSeconds(3));
        return docker.State();
    }

    /// <summary>Stops docking: clears the window's region and owner, and leaves it where it is.</summary>
    [JsonRpcMethod("window.undock")]
    public WindowDockState WindowUndock()
    {
        RequireWindows();
        WindowDocker? docker;
        lock (_dockLock)
        {
            docker = _docker;
            _docker = null;
        }
        if (docker is null)
        {
            return new WindowDockState(false, null, 0, 0, null, null, null, false, false, null, false, null);
        }
        docker.Stop();
        return docker.State();
    }

    /// <summary>Docked or not, the webview's rectangle, the window's, whether it is shown and whether it has the focus.</summary>
    [JsonRpcMethod("window.state")]
    public WindowDockState WindowState()
    {
        RequireWindows();
        WindowDocker? docker;
        lock (_dockLock)
        {
            docker = _docker;
        }
        return docker?.State() ?? new WindowDockState(false, null, 0, 0, null, null, null, false, false, null, false, null);
    }

    /// <summary>Undocks at exit, so the game is not left owned by a window or clipped.</summary>
    public void UndockForExit()
    {
        WindowDocker? docker;
        lock (_dockLock)
        {
            docker = _docker;
            _docker = null;
        }
        docker?.Stop();
    }

    // ---- the release key ------------------------------------------------------------------------

    internal sealed record HotKey(string Text, uint Mods, uint Vk)
    {
        private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Pause"] = 0x13, ["Break"] = 0x13, ["Escape"] = 0x1B, ["Esc"] = 0x1B,
            ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26,
            ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["ScrollLock"] = 0x91,
        };

        public static HotKey Parse(string text)
        {
            uint mods = 0, vk = 0;
            foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                case "ctrl" or "control": mods |= 0x2; continue;
                case "alt": mods |= 0x1; continue;
                case "shift": mods |= 0x4; continue;
                case "win" or "meta": mods |= 0x8; continue;
                }
                if (vk != 0)
                {
                    throw new ArgumentException($"releaseKey has more than one key: '{text}'");
                }
                if (Named.TryGetValue(raw, out var named))
                {
                    vk = named;
                }
                else if (raw.Length == 1 && char.IsAsciiLetterOrDigit(raw[0]))
                {
                    vk = char.ToUpperInvariant(raw[0]);
                }
                else if ((raw[0] is 'F' or 'f') && int.TryParse(raw.AsSpan(1), out var f) && f is >= 1 and <= 24)
                {
                    vk = (uint)(0x6F + f);
                }
                else
                {
                    throw new ArgumentException($"releaseKey: '{raw}' is not a key REDbridge knows (a letter, a digit, F1 to F24, or one of {string.Join(", ", Named.Keys)})");
                }
            }
            if (vk == 0)
            {
                throw new ArgumentException($"releaseKey names no key: '{text}'");
            }
            return new HotKey(text, mods, vk);
        }
    }

    // ---- the loop -------------------------------------------------------------------------------

    /// <summary>
    /// Keeps one window on a webview, on a thread of its own (the release key's messages come to the
    /// thread that registered it). Each tick, every 30 ms: read the webview's rectangle (the element is
    /// kept and found again only when it answers nothing), put the window at its corner (centred when
    /// the webview is bigger) without activating it, clip it with a region to the part inside, hide it
    /// with an empty region when the webview is not shown, and put back its owner, style and place if
    /// the game changed them.
    /// </summary>
    private sealed class WindowDocker
    {
        private const long PopupStyle = 0x96000000L; // WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN
        private const int TickMs = 30;

        private readonly BridgeHost _host;
        private readonly string _class;
        private readonly IntPtr _game, _owner;
        private readonly string _webview;
        private readonly HotKey _key;
        private readonly ManualResetEvent _stop = new(false);
        private readonly ManualResetEventSlim _firstTick = new(false);
        private readonly object _stateLock = new();
        private Thread? _thread;

        private bool _keyRegistered, _shown = true, _focused;
        private WinRect? _webviewRect, _windowRect;
        private string? _ended;
        private (int L, int T, int R, int B)? _region;
        private Uia? _uia;
        private Uia.Element? _element;
        private long _nextFind;
        private int _misses;

        public WindowDocker(BridgeHost host, string cls, IntPtr game, IntPtr owner, string webview, HotKey key)
        {
            _host = host;
            _class = cls;
            _game = game;
            _owner = owner;
            _webview = webview;
            _key = key;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "window.dock" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void WaitFirstTick(TimeSpan timeout) => _firstTick.Wait(timeout);

        public void Stop()
        {
            _stop.Set();
            if (_thread is not null && _thread != Thread.CurrentThread)
            {
                _thread.Join(TimeSpan.FromSeconds(3));
            }
        }

        public WindowDockState State()
        {
            lock (_stateLock)
            {
                return new WindowDockState(_ended is null && _thread is { IsAlive: true }, _class, _game, _owner, _webview, _webviewRect,
                    _windowRect, _shown, _focused, _key.Text, _keyRegistered, _ended);
            }
        }

        private void Notify(string method, object args)
        {
            try
            {
                _host.Rpc?.NotifyWithParameterObjectAsync(method, args);
            }
            catch (Exception e)
            {
                _host.Log.Warning($"window: could not send {method}: {e.Message}");
            }
        }

        private void Run()
        {
            Win.SetThreadDpiAwarenessContext(Win.PerMonitorAwareV2);
            try
            {
                _uia = new Uia();
            }
            catch (Exception e)
            {
                _host.Log.Error($"window: UI Automation did not start: {e.Message}");
            }
            _keyRegistered = Win.RegisterHotKey(IntPtr.Zero, 1, _key.Mods | 0x4000 /* MOD_NOREPEAT */, _key.Vk);
            _host.Log.Info(_keyRegistered
                ? $"window: docking the {_class} window on '{_webview}', release key {_key.Text}"
                : $"window: docking the {_class} window on '{_webview}'; the release key {_key.Text} is taken by another program (error {Marshal.GetLastWin32Error()})");
            Win.SetWindowLongPtr(_game, Win.GwlpHwndParent, _owner);

            var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle() };
            try
            {
                while (true)
                {
                    try
                    {
                        Tick();
                    }
                    catch (Exception e)
                    {
                        _host.Log.Warning($"window: tick failed: {e.Message}");
                    }
                    _firstTick.Set();
                    if (_ended is not null)
                    {
                        break;
                    }
                    while (Win.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
                    {
                        if (msg.Message == 0x0312 /* WM_HOTKEY */)
                        {
                            Release();
                        }
                    }
                    var woke = Win.MsgWaitForMultipleObjects(1, handles, false, TickMs, 0x04FF /* QS_ALLINPUT */);
                    if (woke == 0)
                    {
                        break; // stopped
                    }
                }
            }
            finally
            {
                if (_keyRegistered)
                {
                    Win.UnregisterHotKey(IntPtr.Zero, 1);
                }
                if (Win.IsWindow(_game))
                {
                    Win.SetWindowRgn(_game, IntPtr.Zero, true);
                    Win.SetWindowLongPtr(_game, Win.GwlpHwndParent, IntPtr.Zero);
                }
                lock (_stateLock)
                {
                    _ended ??= "undocked";
                }
                _uia?.Dispose();
                _firstTick.Set();
                _host.Log.Info($"window: docking ended ({_ended})");
            }
        }

        private void Release()
        {
            Win.ClipCursor(IntPtr.Zero);
            var focused = Win.SetForegroundWindow(_owner);
            _host.Log.Info($"window: release key pressed; pointer freed, focus to the owner {(focused ? "given" : "refused by Windows")}");
            Notify("window.released", new { key = _key.Text, focused });
        }

        private void End(string why)
        {
            lock (_stateLock)
            {
                _ended = why;
            }
            Notify("window.lost", new { reason = why });
        }

        private void Tick()
        {
            if (!Win.IsWindow(_game))
            {
                End("window");
                return;
            }
            if (!Win.IsWindow(_owner))
            {
                End("owner");
                return;
            }

            // Strip the frame the game's Windowed mode adds, keeping its client size: the window is
            // never resized, so the game's picture and its pointer stay one to one.
            var style = Win.GetWindowLongPtr(_game, Win.GwlStyle).ToInt64();
            Win.GetClientRect(_game, out var client);
            int gw = client.R, gh = client.B;
            Win.GetWindowRect(_game, out var place);
            var framed = (style & 0x00C40000L) != 0 || (style & 0x80000000L) == 0; // WS_CAPTION | WS_THICKFRAME, or not a popup
            if (framed)
            {
                Win.SetWindowLongPtr(_game, Win.GwlStyle, new IntPtr(PopupStyle));
                Win.SetWindowPos(_game, IntPtr.Zero, place.L, place.T, gw, gh, 0x0010 | 0x0004 | 0x0020 /* NOACTIVATE | NOZORDER | FRAMECHANGED */);
                Win.GetWindowRect(_game, out place);
                _host.Log.Info($"window: frame taken off ({gw}x{gh}, style was 0x{style:X})");
            }
            if (Win.GetWindowLongPtr(_game, Win.GwlpHwndParent) != _owner)
            {
                Win.SetWindowLongPtr(_game, Win.GwlpHwndParent, _owner);
            }

            var r = Win.IsIconic(_owner) ? null : ReadWebview();
            var show = r is { Width: >= 8, Height: >= 8 };
            if (!show)
            {
                if (_shown || _region is not null)
                {
                    Win.SetWindowRgn(_game, Win.CreateRectRgn(0, 0, 0, 0), true);
                    _region = null;
                    SetShown(false, Win.IsIconic(_owner) ? "owner minimised" : "webview not shown");
                }
            }
            else
            {
                var x = r!.X + Math.Max(0, (r.Width - gw) / 2);
                var y = r.Y + Math.Max(0, (r.Height - gh) / 2);
                if (place.L != x || place.T != y)
                {
                    Win.SetWindowPos(_game, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0010 | 0x0004 /* NOSIZE | NOACTIVATE | NOZORDER */);
                    Win.GetWindowRect(_game, out place);
                }
                // The part of the window inside the webview, in the window's own coordinates.
                var region = (Math.Max(r.X, x) - x, Math.Max(r.Y, y) - y, Math.Min(r.X + r.Width, x + gw) - x, Math.Min(r.Y + r.Height, y + gh) - y);
                if (_region != region)
                {
                    var rgn = Win.CreateRectRgn(region.Item1, region.Item2, region.Item3, region.Item4);
                    if (Win.SetWindowRgn(_game, rgn, true) == 0)
                    {
                        Win.DeleteObject(rgn);
                    }
                    _region = region;
                }
                SetShown(true, null);
            }

            var focused = Win.GetForegroundWindow() == _game;
            bool focusChanged;
            lock (_stateLock)
            {
                _webviewRect = r;
                _windowRect = new WinRect(place.L, place.T, place.R - place.L, place.B - place.T);
                focusChanged = focused != _focused;
                _focused = focused;
            }
            if (focusChanged)
            {
                Notify("window.focus", new { focused });
            }
        }

        private void SetShown(bool shown, string? why)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = shown != _shown;
                _shown = shown;
            }
            if (changed)
            {
                _host.Log.Info(shown ? "window: shown on the webview" : $"window: hidden ({why})");
                Notify("window.shown", new { shown, why });
            }
        }

        // The webview's rectangle: the kept element's (about 1 ms); found again when it answers
        // nothing (8 to 17 ms a find), at most every 250 ms while it stays missing.
        private WinRect? ReadWebview()
        {
            if (_uia is null)
            {
                return null;
            }
            if (_element is not null)
            {
                var kept = _element.Rect();
                if (kept is { Width: > 0, Height: > 0 })
                {
                    return kept;
                }
                _element.Dispose();
                _element = null;
            }
            var now = Environment.TickCount64;
            if (now < _nextFind)
            {
                return null;
            }
            _element = _uia.FindDocument(_owner, _webview);
            var found = _element?.Rect();
            if (found is { Width: > 0, Height: > 0 })
            {
                _misses = 0;
                return found;
            }
            _element?.Dispose();
            _element = null;
            _misses++;
            _nextFind = now + 250;
            return null;
        }
    }

    // ---- UI Automation, through COM -------------------------------------------------------------

    /// <summary>The few UI Automation calls the loop needs, through COM interop (the managed
    /// System.Windows.Automation needs the Windows Desktop runtime, which REDbridge's installs lack).</summary>
    private sealed class Uia : IDisposable
    {
        private const int TreeScopeDescendants = 4;
        private const int BoundingRectangleProperty = 30001;
        private const int ControlTypeProperty = 30003;
        private const int NameProperty = 30005;
        private const int DocumentControlType = 50030;

        private IUIAutomation? _uia;

        public Uia()
        {
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), true)!;
            _uia = (IUIAutomation)Activator.CreateInstance(type)!;
        }

        public Element? FindDocument(IntPtr owner, string name)
        {
            if (_uia is null)
            {
                return null;
            }
            try
            {
                var root = _uia.ElementFromHandle(owner);
                var isDocument = _uia.CreatePropertyCondition(ControlTypeProperty, DocumentControlType);
                var named = _uia.CreatePropertyCondition(NameProperty, name);
                var both = _uia.CreateAndCondition(isDocument, named);
                var found = root.FindFirst(TreeScopeDescendants, both);
                Marshal.ReleaseComObject(both);
                Marshal.ReleaseComObject(named);
                Marshal.ReleaseComObject(isDocument);
                Marshal.ReleaseComObject(root);
                return found is null ? null : new Element(found);
            }
            catch (COMException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_uia is not null)
            {
                Marshal.ReleaseComObject(_uia);
                _uia = null;
            }
        }

        public sealed class Element(IUIAutomationElement element) : IDisposable
        {
            private IUIAutomationElement? _element = element;

            public WinRect? Rect()
            {
                try
                {
                    if (_element?.GetCurrentPropertyValue(BoundingRectangleProperty) is double[] { Length: 4 } r)
                    {
                        return new WinRect((int)Math.Round(r[0]), (int)Math.Round(r[1]), (int)Math.Round(r[2]), (int)Math.Round(r[3]));
                    }
                }
                catch (COMException)
                {
                }
                catch (InvalidCastException)
                {
                }
                return null;
            }

            public void Dispose()
            {
                if (_element is not null)
                {
                    Marshal.ReleaseComObject(_element);
                    _element = null;
                }
            }
        }
    }

    // Slots in uiautomationclient.h's order (Windows SDK 10.0.26100); only the named ones are called.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void GetControlViewWalker();
        void GetContentViewWalker();
        void GetRawViewWalker();
        void GetRawViewCondition();
        void GetControlViewCondition();
        void GetContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
        void CreatePropertyConditionEx();
        IUIAutomationCondition CreateAndCondition(IUIAutomationCondition condition1, IUIAutomationCondition condition2);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationCondition
    {
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }

    // ---- user32 ---------------------------------------------------------------------------------

    internal static partial class Win
    {
        public const int GwlStyle = -16, GwlExStyle = -20, GwlpHwndParent = -8;
        public static readonly IntPtr PerMonitorAwareV2 = new(-4);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int PriClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string? cls, string? title);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect r);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr r);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out Msg m, IntPtr h, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint ms, uint mask);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        public static string ClassOf(IntPtr h)
        {
            var s = new StringBuilder(256);
            GetClassNameW(h, s, s.Capacity);
            return s.ToString();
        }

        public static string TitleOf(IntPtr h)
        {
            var s = new StringBuilder(512);
            GetWindowTextW(h, s, s.Capacity);
            return s.ToString();
        }

        public static int PidOf(IntPtr h)
        {
            GetWindowThreadProcessId(h, out var pid);
            return (int)pid;
        }

        private static List<IntPtr> TopLevel()
        {
            var all = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                all.Add(h);
                return true;
            }, IntPtr.Zero);
            return all;
        }

        public static IntPtr FindTop(string cls, int? pid)
        {
            if (pid is null)
            {
                return FindWindowW(cls, null);
            }
            return TopLevel().FirstOrDefault(h => PidOf(h) == pid && ClassOf(h) == cls);
        }

        // A process and its parents, a few levels up: REDcode knows its extension host's process id,
        // whose parent is the VS Code process that owns the window.
        private static HashSet<int> WithParents(int pid)
        {
            var parents = new Dictionary<int, int>();
            var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap != new IntPtr(-1))
            {
                var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    parents[(int)e.ProcessId] = (int)e.ParentProcessId;
                }
                CloseHandle(snap);
            }
            var set = new HashSet<int> { pid };
            for (int p = pid, i = 0; i < 6 && parents.TryGetValue(p, out var parent) && parent != 0 && set.Add(parent); i++)
            {
                p = parent;
            }
            return set;
        }

        public static IntPtr FindOwner(OwnerSpec spec)
        {
            if (spec.Hwnd is long hwnd and not 0)
            {
                return IsWindow(new IntPtr(hwnd)) ? new IntPtr(hwnd) : IntPtr.Zero;
            }
            if (spec.Pid is null && string.IsNullOrEmpty(spec.Title))
            {
                throw new ArgumentException("owner needs hwnd, pid or title");
            }
            var pids = spec.Pid is int pid ? WithParents(pid) : null;
            return TopLevel().FirstOrDefault(h =>
                IsWindowVisible(h) &&
                GetWindowLongPtr(h, GwlpHwndParent) == IntPtr.Zero &&
                (pids is null || pids.Contains(PidOf(h))) &&
                (string.IsNullOrEmpty(spec.Title) || TitleOf(h).Contains(spec.Title, StringComparison.OrdinalIgnoreCase)) &&
                TitleOf(h).Length > 0);
        }

        public static WindowFound Describe(IntPtr h)
        {
            GetWindowRect(h, out var r);
            GetClientRect(h, out var c);
            return new WindowFound(h.ToInt64(), PidOf(h), ClassOf(h), TitleOf(h), new WinRect(r.L, r.T, r.R - r.L, r.B - r.T), c.R, c.B,
                $"0x{GetWindowLongPtr(h, GwlStyle).ToInt64():X}", $"0x{GetWindowLongPtr(h, GwlExStyle).ToInt64():X}", IsWindowVisible(h),
                IsIconic(h), GetForegroundWindow() == h, GetWindowLongPtr(h, GwlpHwndParent).ToInt64());
        }
    }
}
