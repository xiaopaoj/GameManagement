using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GameManagement.Models;

namespace GameManagement.Services;

[Flags]
public enum BossKeyModifiers : uint
{
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed record BossKeyConfiguration(bool Enabled, BossKeyModifiers Modifiers, int VirtualKey);

public sealed class BossModeController : IDisposable
{
    private const int HotKeyId = 0x474D;
    private const int WmHotKey = 0x0312;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private readonly Window _applicationWindow;
    private readonly Func<IEnumerable<GameItem>> _games;
    private readonly List<nint> _hiddenWindows = [];
    private readonly Dictionary<string, bool> _previousMuteStates = new(StringComparer.Ordinal);
    private HwndSource? _source;
    private bool _registered;
    private bool _bossModeActive;

    public BossModeController(Window applicationWindow, Func<IEnumerable<GameItem>> games)
    {
        _applicationWindow = applicationWindow;
        _games = games;
    }

    public bool IsActive => _bossModeActive;

    public bool Configure(BossKeyConfiguration configuration, out string error)
    {
        error = string.Empty;
        EnsureSource();
        Unregister();
        if (!configuration.Enabled) return true;
        if (configuration.Modifiers == 0 || configuration.VirtualKey <= 0)
        {
            error = "老板键必须至少包含一个修饰键和一个普通按键。";
            return false;
        }
        if (!RegisterHotKey(_source!.Handle, HotKeyId, (uint)configuration.Modifiers | 0x4000, (uint)configuration.VirtualKey))
        {
            error = "该组合键已被其他程序占用，请更换后重试。";
            return false;
        }
        _registered = true;
        return true;
    }

    public void Toggle()
    {
        if (_bossModeActive) Restore();
        else Hide();
    }

    private void Hide()
    {
        var gameProcessIds = _games().Where(game => game.RunningProcessId.HasValue).Select(game => (uint)game.RunningProcessId!.Value).Distinct().ToHashSet();
        var windowProcessIds = gameProcessIds.Append((uint)Environment.ProcessId).ToHashSet();
        _hiddenWindows.Clear();
        EnumWindows((window, parameter) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (windowProcessIds.Contains(processId) && IsWindowVisible(window))
            {
                _hiddenWindows.Add(window);
                ShowWindow(window, SwHide);
            }
            return true;
        }, nint.Zero);
        _previousMuteStates.Clear();
        foreach (var state in GameAudioMuteService.SetMuted(gameProcessIds, true)) _previousMuteStates[state.Key] = state.Value;
        _bossModeActive = true;
    }

    private void Restore()
    {
        foreach (var window in _hiddenWindows.Where(IsWindow)) _ = ShowWindow(window, SwShow);
        GameAudioMuteService.Restore(_previousMuteStates);
        _hiddenWindows.Clear();
        _previousMuteStates.Clear();
        _bossModeActive = false;
        _applicationWindow.Show();
        if (_applicationWindow.WindowState == WindowState.Minimized) _applicationWindow.WindowState = WindowState.Normal;
        _applicationWindow.Activate();
    }

    private void EnsureSource()
    {
        if (_source is not null) return;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_applicationWindow).Handle)
            ?? throw new InvalidOperationException("无法创建老板键窗口消息监听。请重新打开软件后重试。");
        _source.AddHook(WindowMessageHook);
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            Toggle();
        }
        return nint.Zero;
    }

    private void Unregister()
    {
        if (!_registered || _source is null) return;
        _ = UnregisterHotKey(_source.Handle, HotKeyId);
        _registered = false;
    }

    public void Dispose()
    {
        if (_bossModeActive) Restore();
        Unregister();
        if (_source is not null) _source.RemoveHook(WindowMessageHook);
        _source = null;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

internal static class GameAudioMuteService
{
    public static IReadOnlyDictionary<string, bool> SetMuted(IReadOnlySet<uint> processIds, bool muted)
    {
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        VisitSessions((sessionId, processId, volume) =>
        {
            if (!processIds.Contains(processId)) return;
            _ = volume.GetMute(out var wasMuted);
            states.TryAdd(sessionId, wasMuted);
            _ = volume.SetMute(muted, Guid.Empty);
        });
        return states;
    }

    public static void Restore(IReadOnlyDictionary<string, bool> states) => VisitSessions((sessionId, processId, volume) =>
    {
        if (states.TryGetValue(sessionId, out var muted)) _ = volume.SetMute(muted, Guid.Empty);
    });

    private static void VisitSessions(Action<string, uint, ISimpleAudioVolume> visitor)
    {
        IMMDeviceEnumerator? enumerator = null; IMMDevice? device = null; IAudioSessionManager2? manager = null; IAudioSessionEnumerator? sessions = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(typeof(MMDeviceEnumeratorComObject))!;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
            var iid = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, nint.Zero, out var managerObject));
            manager = (IAudioSessionManager2)managerObject;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));
            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (sessions.GetSession(index, out control) < 0 || control is not IAudioSessionControl2 control2 || control is not ISimpleAudioVolume volume) continue;
                    if (control2.GetProcessId(out var processId) >= 0 && control2.GetSessionInstanceIdentifier(out var sessionId) >= 0)
                        visitor(sessionId, processId, volume);
                }
                catch { }
                finally { if (control is not null) Marshal.FinalReleaseComObject(control); }
            }
        }
        catch (COMException ex) { AppLogger.Error("老板模式调整游戏音频失败", ex); }
        finally
        {
            if (sessions is not null) Marshal.FinalReleaseComObject(sessions);
            if (manager is not null) Marshal.FinalReleaseComObject(manager);
            if (device is not null) Marshal.FinalReleaseComObject(device);
            if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private sealed class MMDeviceEnumeratorComObject { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator { [PreserveSig] int NotImpl1(); [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int context, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance); }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2 { [PreserveSig] int NotImpl1(); [PreserveSig] int NotImpl2(); [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator); }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator { [PreserveSig] int GetCount(out int count); [PreserveSig] int GetSession(int index, out IAudioSessionControl control); }
    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl { }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int NotImpl1(); [PreserveSig] int NotImpl2(); [PreserveSig] int NotImpl3(); [PreserveSig] int NotImpl4(); [PreserveSig] int NotImpl5(); [PreserveSig] int NotImpl6(); [PreserveSig] int NotImpl7(); [PreserveSig] int NotImpl8(); [PreserveSig] int NotImpl9();
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        [PreserveSig] int GetProcessId(out uint processId);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume { [PreserveSig] int SetMasterVolume(float level, Guid context); [PreserveSig] int GetMasterVolume(out float level); [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, Guid context); [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted); }
}
