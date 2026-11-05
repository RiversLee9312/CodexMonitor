using System.ComponentModel;
using System.Runtime.InteropServices;
using CodexMonitor.App;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace CodexMonitor.UiTests;

/// <summary>OS input for our test window. Scrolling is never driven by ChangeView or UIA scroll percentages.</summary>
internal sealed class InjectedInput(MainWindow window)
{
    private readonly nint _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
    internal string KeyboardDiagnostic => $"window={_handle}, foreground={GetForegroundWindow()}, nativeFocus={GetFocus()}, thread={GetCurrentThreadId()}, endScan={MapVirtualKey(0x23,0)}, inputSize={Marshal.SizeOf<NativeInput>()}, unionOffset={Marshal.OffsetOf<NativeInput>(nameof(NativeInput.Data))}";
    internal async Task ActivateAsync()
    {
        var current = GetCurrentThreadId(); var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = foreground != 0 && current != foreground && AttachThreadInput(current, foreground, true);
        try { window.Activate(); BringWindowToTop(_handle); SetForegroundWindow(_handle); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
        await Task.Delay(120);
        EnsureTarget();
    }
    private void EnsureTarget()
    {
        if (GetAncestor(GetForegroundWindow(), 3) != GetAncestor(_handle, 3))
        {
            var foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, out var process);
            throw new InvalidOperationException("Input injection stopped because the test window lost foreground ownership: expected " + _handle + ", actual " + foreground + ", process=" + process);
        }
    }
    internal Point ScreenPoint(FrameworkElement element, double x = .5, double y = .5)
    {
        var local = element.TransformToVisual(window.ReviewRoot).TransformPoint(new Point(element.ActualWidth * x, element.ActualHeight * y));
        var scale = window.ReviewRoot.XamlRoot.RasterizationScale;
        var point = new NativePoint { X = (int)Math.Round(local.X * scale), Y = (int)Math.Round(local.Y * scale) };
        if (!GetClientRect(_handle, out var bounds) || point.X < 0 || point.Y < 0 || point.X >= bounds.Right || point.Y >= bounds.Bottom)
            throw new InvalidOperationException("The input target is outside the test window's client area");
        if (!ClientToScreen(_handle, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(point.X, point.Y);
    }
    internal void Move(Point point)
    {
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (point.X < left || point.Y < top || point.X >= left + width || point.Y >= top + height)
            throw new InvalidOperationException("The injected pointer must stay inside the desktop");
        Send(new NativeInput { Mouse = new() { X = (int)Math.Round((point.X - left) * 65535 / (width - 1)),
            Y = (int)Math.Round((point.Y - top) * 65535 / (height - 1)), Flags = 0xC001 } });
    }
    private void Send(params NativeInput[] values)
    {
        EnsureTarget();
        if (SendInput((uint)values.Length, values, Marshal.SizeOf<NativeInput>()) != values.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OS input injection failed");
    }
    internal async Task WheelAsync(FrameworkElement surface, int delta, int bursts, int interval = 6, double x = .6, double y = .5, bool positionPointer = true)
    {
        if (positionPointer) Move(ScreenPoint(surface, x, y));
        for (int i = 0; i < bursts; i++)
        {
            Send(new NativeInput { Mouse = new() { MouseData = unchecked((uint)delta), Flags = 0x0800 } });
            await Task.Delay(interval);
        }
    }
    internal async Task KeyAsync(ushort key, int repeats = 1)
    {
        for (int i = 0; i < repeats; i++)
        {
            var flags = 8u | (key is >= 0x21 and <= 0x28 ? 1u : 0);
            var scan = (ushort)MapVirtualKey(key, 0);
            Send(new NativeInput { Type = 1, Keyboard = new() { Scan = scan, Flags = flags } });
            await Task.Delay(40);
            Send(new NativeInput { Type = 1, Keyboard = new() { Scan = scan, Flags = flags | 2 } });
            await Task.Delay(40);
        }
    }
    internal async Task ClickAsync(FrameworkElement element, double x = .5, double y = .5)
    {
        Move(ScreenPoint(element, x, y));
        Send(new NativeInput { Mouse = new() { Flags = 2 } }, new NativeInput { Mouse = new() { Flags = 4 } });
        await Task.Delay(80);
    }
    internal async Task DragAsync(Point from, Point to, int steps = 24)
    {
        Move(from); Send(new NativeInput { Mouse = new() { Flags = 2 } });
        try
        {
            for (int i = 1; i <= steps; i++)
            { Move(new(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps)); await Task.Delay(10); }
        }
        finally { Send(new NativeInput { Mouse = new() { Flags = 4 } }); }
        await Task.Delay(80);
    }
    internal async Task TouchSwipeAsync(FrameworkElement surface, bool towardsEarlier, int steps = 20)
    {
        EnsureTarget();
        var device = CreateSyntheticPointerDevice(2, 1, 3);
        if (device == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Touch injection is unavailable");
        var from = ScreenPoint(surface, .6, towardsEarlier ? .2 : .8);
        var to = ScreenPoint(surface, .6, towardsEarlier ? .8 : .2);
        void Packet(Point point, uint flags)
        {
            EnsureTarget();
            var x = (int)point.X; var y = (int)point.Y;
            var contact = new TouchInfo { Pointer = new() { Type = 2, PointerId = 0, Flags = flags, Pixel = new() { X = x, Y = y } }, Mask = 7,
                Contact = new() { Left = x - 3, Top = y - 3, Right = x + 3, Bottom = y + 3 }, Orientation = 90, Pressure = 512 };
            if (!InjectSyntheticPointerInput(device, [new() { Type = 2, Touch = contact }], 1))
            { var error = Marshal.GetLastWin32Error(); throw new Win32Exception(error, $"Touch packet was rejected: error={error}, flags={flags:X}, point={x},{y}, desktop={GetSystemMetrics(78)}x{GetSystemMetrics(79)}, infoSize={Marshal.SizeOf<PointerInfo>()}, touchSize={Marshal.SizeOf<TouchInfo>()}"); }
        }
        var last = from;
        try
        {
            Packet(from, 0x10006); await Task.Delay(12);
            for (int i = 1; i <= steps; i++)
            { last = new(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps); Packet(last, 0x20006); await Task.Delay(8); }
            Packet(last, 0x40000);
        }
        finally { DestroySyntheticPointerDevice(device); }
        await Task.Delay(100);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput
    {
        public uint Type; public InputUnion Data;
        public MouseInput Mouse { set => Data.Mouse = value; }
        public KeyboardInput Keyboard { set => Data.Keyboard = value; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo
    {
        public uint Type, PointerId, FrameId, Flags; public nint Device, Target;
        public NativePoint Pixel, Himetric, RawPixel, RawHimetric;
        public uint Time, History; public int Input; public uint Keys; public ulong Performance; public uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TouchInfo { public PointerInfo Pointer; public uint Flags, Mask; public NativeRect Contact, RawContact; public uint Orientation, Pressure; }
    [StructLayout(LayoutKind.Explicit, Size = 152)] private struct PointerTypeInfo { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public TouchInfo Touch; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateSyntheticPointerDevice(uint type, uint contacts, uint mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectSyntheticPointerInput(nint device, [In] PointerTypeInfo[] contacts, uint count);
    [DllImport("user32.dll")] private static extern void DestroySyntheticPointerDevice(nint device);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern nint GetFocus();
}
