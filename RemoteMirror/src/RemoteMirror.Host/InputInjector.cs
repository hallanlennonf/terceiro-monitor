using System.Runtime.InteropServices;
using RemoteMirror.Core;

namespace RemoteMirror.Host;

/// <summary>
/// Injeta mouse e teclado na sessao local via SendInput.
///
/// SendInput entrega os eventos na fila de entrada da sessao atual, como se
/// viessem do hardware. Por isso o usuario local continua com teclado e mouse
/// funcionando ao mesmo tempo — nada e "tomado" dele, os eventos apenas se
/// misturam. E tambem por isso nada aqui bloqueia a tela.
///
/// Limite conhecido (UIPI): se o host roda sem elevacao, o Windows recusa
/// a injecao em janelas de processos elevados.
/// </summary>
public static class InputInjector
{
    /// <summary>Move o cursor para um ponto em coordenadas do monitor capturado.</summary>
    public static void MoveMouse(MonitorInfo monitor, int x, int y)
    {
        var screenX = monitor.Left + Math.Clamp(x, 0, monitor.Width - 1);
        var screenY = monitor.Top + Math.Clamp(y, 0, monitor.Height - 1);

        var vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (vWidth <= 1 || vHeight <= 1) return;

        // SendInput absoluto trabalha numa grade normalizada de 0..65535
        // sobre o desktop virtual inteiro, nao sobre um monitor.
        var nx = (int)(((long)(screenX - vLeft) * 65535) / (vWidth - 1));
        var ny = (int)(((long)(screenY - vTop) * 65535) / (vHeight - 1));

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                }
            }
        });
    }

    /// <summary>
    /// Empurra o cursor por um deslocamento relativo.
    ///
    /// O modo "terceiro monitor" nao pode usar coordenadas absolutas: o cursor
    /// do host tem posicao propria e so precisa ser deslocado, como faria um
    /// mouse fisico ligado nele.
    /// </summary>
    public static void MoveMouseRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE }
            }
        });
    }

    /// <summary>Coloca o cursor num ponto absoluto do desktop (coordenadas de tela).</summary>
    public static void SetCursorPosition(int x, int y) => SetCursorPos(x, y);

    /// <summary>Posicao atual do cursor, em coordenadas de tela.</summary>
    public static (int X, int Y) GetCursorPosition() =>
        GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>Limites do desktop virtual do host (todos os monitores dele).</summary>
    public static DesktopBounds GetDesktopBounds()
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        return new DesktopBounds(
            left,
            top,
            left + GetSystemMetrics(SM_CXVIRTUALSCREEN),
            top + GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    /// <summary>Pressiona ou solta um botao do mouse na posicao atual.</summary>
    public static void MouseButton(MouseButtonCode button, bool down)
    {
        var flags = button switch
        {
            MouseButtonCode.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButtonCode.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButtonCode.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };
        if (flags == 0) return;

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } }
        });
    }

    /// <summary>Roda do mouse. delta positivo rola para cima (120 por clique).</summary>
    public static void MouseWheel(int delta)
    {
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = MOUSEEVENTF_WHEEL,
                }
            }
        });
    }

    /// <summary>Pressiona ou solta uma tecla, por virtual-key code.</summary>
    public static void Key(ushort virtualKey, bool down)
    {
        var scan = (ushort)MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);
        var flags = KEYEVENTF_SCANCODE;

        if (!down) flags |= KEYEVENTF_KEYUP;
        if (IsExtendedKey(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        // Teclas sem scan code (ex.: algumas multimidia) vao por virtual-key.
        if (scan == 0)
        {
            flags = down ? 0u : KEYEVENTF_KEYUP;
            Send(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = flags } }
            });
            return;
        }

        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } }
        });
    }

    /// <summary>
    /// Solta tudo que possa ter ficado preso quando o viewer cai no meio de
    /// um atalho. Sem isto o notebook fica, por exemplo, com Alt grudado.
    /// </summary>
    public static void ReleaseAllModifiers()
    {
        ushort[] modifiers =
        [
            0x10, 0x11, 0x12,       // Shift, Ctrl, Alt
            0xA0, 0xA1,             // LShift, RShift
            0xA2, 0xA3,             // LCtrl, RCtrl
            0xA4, 0xA5,             // LAlt, RAlt
            0x5B, 0x5C,             // LWin, RWin
        ];

        foreach (var vk in modifiers)
            Key(vk, down: false);
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or      // PageUp, PageDown
        0x23 or 0x24 or      // End, Home
        0x25 or 0x26 or      // Left, Up
        0x27 or 0x28 or      // Right, Down
        0x2D or 0x2E or      // Insert, Delete
        0x90 or              // NumLock
        0x6F or              // Divide (numpad /)
        0x0D or              // Enter (cobre o do numpad)
        0xA3 or 0xA5 or      // RCtrl, RAlt
        0x5B or 0x5C or 0x5D; // LWin, RWin, Apps

    private static void Send(INPUT input)
    {
        var inputs = new[] { input };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    // ------------------------------------------------------------- interop

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
