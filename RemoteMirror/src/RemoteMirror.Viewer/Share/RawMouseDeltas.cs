using System.Runtime.InteropServices;

namespace RemoteMirror.Viewer.Share;

/// <summary>
/// Deslocamento fisico do mouse, lido direto do dispositivo (Raw Input).
///
/// Por que isto existe, e nao basta a posicao do cursor: quando o cursor
/// encosta na borda da tela o Windows para de move-lo, e a posicao relatada
/// deixa de acompanhar o mouse - ela oscila em torno do ponto de parada. A
/// diferenca entre posicoes entao alterna entre +1 e -1, e qualquer regra que
/// some "empurrao" sobre ela nunca acumula nada. Medido aqui, o mesmo gesto
/// aparece como uma sequencia limpa de deslocamentos positivos.
///
/// Esta janela nao aparece e nao recebe foco: RIDEV_INPUTSINK entrega os
/// eventos mesmo quando outro programa esta em uso.
/// </summary>
public sealed class RawMouseDeltas : NativeWindow, IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_CLOSE = 0x0010;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

    private readonly uint _headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
    private IntPtr _buffer;
    private int _bufferSize;
    private bool _disposed;

    /// <summary>Deslocamento relativo do mouse, em unidades do dispositivo.</summary>
    public event Action<int, int>? Moved;

    public RawMouseDeltas()
    {
        CreateHandle(new CreateParams
        {
            Caption = "RemoteMirrorRawInput",
            Style = 0,
            ExStyle = 0,
            Height = 0,
            Width = 0,
            X = 0,
            Y = 0,
            Parent = IntPtr.Zero,
        });

        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01,   // generic desktop
            usUsage = 0x02,       // mouse
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = Handle,
        };

        if (!RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException(
                "Nao consegui registrar a leitura do mouse (Raw Input). " +
                $"Erro {Marshal.GetLastWin32Error()}.");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            try { Ler(m.LParam); }
            catch { /* um evento perdido nao justifica derrubar a entrada */ }
        }

        base.WndProc(ref m);
    }

    private void Ler(IntPtr handle)
    {
        uint size = 0;
        GetRawInputData(handle, RID_INPUT, IntPtr.Zero, ref size, _headerSize);
        if (size == 0) return;

        if (_buffer == IntPtr.Zero || _bufferSize < (int)size)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)size);
            _bufferSize = (int)size;
        }

        if (GetRawInputData(handle, RID_INPUT, _buffer, ref size, _headerSize) != size) return;

        var raw = Marshal.PtrToStructure<RAWINPUTMOUSE>(_buffer);

        // dwType 0 e mouse. Tablets e touch reportam posicao absoluta, que nao
        // serve para medir empurrao: ignoramos.
        if (raw.header.dwType != 0) return;
        if ((raw.usFlags & MOUSE_MOVE_ABSOLUTE) != 0) return;
        if (raw.lLastX == 0 && raw.lLastY == 0) return;

        Moved?.Invoke(raw.lLastX, raw.lLastY);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            SendMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            DestroyHandle();
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTMOUSE
    {
        public RAWINPUTHEADER header;
        public ushort usFlags;
        public ushort padding;
        public uint ulButtons;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
