using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteMirror.Viewer.Share;

/// <summary>Um evento de mouse visto pelo hook, antes do Windows processar.</summary>
public readonly record struct MouseHookEvent(
    int Message, int X, int Y, int WheelDelta, bool Injected);

/// <summary>Um evento de teclado visto pelo hook.</summary>
public readonly record struct KeyHookEvent(ushort VirtualKey, bool Down, bool Injected);

/// <summary>
/// Hooks globais de mouse e teclado (WH_MOUSE_LL / WH_KEYBOARD_LL).
///
/// Os handlers devolvem true para ENGOLIR o evento: e assim que o cursor
/// "desaparece" deste PC enquanto esta do lado do notebook. Como o Windows
/// chama estes callbacks na fila de entrada, eles precisam ser rapidos —
/// nada de I/O, so enfileirar.
/// </summary>
public sealed class InputHooks : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint LLKHF_INJECTED = 0x10;

    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;

    // Guardados em campo: se o delegate for coletado, o Windows chama um
    // ponteiro morto e o processo morre sem explicacao.
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyProc;

    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private bool _disposed;

    /// <summary>Devolva true para engolir o evento.</summary>
    public event Func<MouseHookEvent, bool>? Mouse;

    /// <summary>Devolva true para engolir o evento.</summary>
    public event Func<KeyHookEvent, bool>? Key;

    public InputHooks()
    {
        _mouseProc = MouseCallback;
        _keyProc = KeyCallback;
    }

    public bool IsInstalled => _mouseHook != IntPtr.Zero;

    /// <summary>
    /// Instala os hooks. Precisa ser chamado de uma thread com bomba de
    /// mensagens (a thread de UI), senao o Windows nunca entrega os eventos.
    /// </summary>
    public void Install()
    {
        if (IsInstalled) return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        var handle = GetModuleHandle(module.ModuleName);

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, handle, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, handle, 0);

        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
        {
            Uninstall();
            throw new InvalidOperationException(
                "Nao consegui instalar os hooks de mouse e teclado. " +
                "Algum software de seguranca pode estar bloqueando.");
        }
    }

    public void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && Mouse is not null)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            var evt = new MouseHookEvent(
                Message: (int)wParam,
                X: data.pt.X,
                Y: data.pt.Y,
                WheelDelta: unchecked((short)(data.mouseData >> 16)),
                Injected: (data.flags & LLMHF_INJECTED) != 0);

            try
            {
                if (Mouse(evt)) return 1; // engolido
            }
            catch
            {
                // Um erro aqui nao pode derrubar a fila de entrada do Windows.
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr KeyCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && Key is not null)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;

            var evt = new KeyHookEvent(
                VirtualKey: (ushort)data.vkCode,
                Down: msg is 0x0100 or 0x0104,   // WM_KEYDOWN / WM_SYSKEYDOWN
                Injected: (data.flags & LLKHF_INJECTED) != 0);

            try
            {
                if (Key(evt)) return 1;
            }
            catch
            {
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }

    // ------------------------------------------------------------- interop

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
