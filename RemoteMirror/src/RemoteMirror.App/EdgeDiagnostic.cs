using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RemoteMirror.Core;
using RemoteMirror.Viewer.Share;

namespace RemoteMirror.App;

/// <summary>
/// Diagnostico da travessia: compara as duas formas de medir o empurrao
/// contra a borda e exercita a cadeia real de deteccao.
///
/// Existe por causa de um bug concreto. A primeira versao media o empurrao
/// pela diferenca entre posicoes do cursor, e nao atravessava nunca: com o
/// cursor encostado na borda o Windows oscila a posicao relatada, entao um
/// gesto continuo chega como +1, -1, +1, -1 e a pressao acumulada zerava a
/// cada leitura negativa. O deslocamento bruto do mouse (Raw Input) nao sofre
/// disso. Este modo mostra os dois lado a lado e diz se a travessia dispara.
/// </summary>
internal sealed class EdgeDiagnostic : Form
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

    private readonly StringBuilder _log = new();
    private readonly HookProc _proc;
    private readonly string _path;

    private readonly EdgeExitDetector _detector =
        EdgeExitDetector.PushingAgainst(ScreenEdge.Right);

    private RawMouseDeltas? _raw;
    private IntPtr _hook;

    private int _lastX, _lastY;
    private bool _hasLast;

    private int _eventosHook;
    private int _eventosNaBorda;
    private int _dxPositivoNaBorda;
    private int _dxNegativoNaBorda;
    private int _rawEventos;
    private int _rawNaBorda;
    private int _travessias;

    public EdgeDiagnostic(string path)
    {
        _path = path;
        _proc = MouseCallback;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0.01;
        Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-10000, -10000);

        var vs = SystemInformation.VirtualScreen;
        Escrever($"VirtualScreen: Left={vs.Left} Top={vs.Top} Right={vs.Right} Bottom={vs.Bottom}");
        Escrever("Monitores:");

        var i = 1;
        foreach (var s in Screen.AllScreens)
            Escrever($"  {i++}: {s.Bounds} principal={s.Primary}");

        Escrever($"DPI do processo: {DeviceDpi}");
        Escrever("");
        Escrever("Empurre o mouse contra a borda DIREITA e insista uns segundos.");
        Escrever("");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(module.ModuleName), 0);

        Escrever(_hook == IntPtr.Zero
            ? $"FALHA ao instalar o hook (erro {Marshal.GetLastWin32Error()})"
            : "hook instalado");

        // A mesma classe que o modo terceiro monitor usa de verdade.
        try
        {
            _raw = new RawMouseDeltas();
            _raw.Moved += OnRaw;
            Escrever("RawMouseDeltas ligado");
        }
        catch (Exception ex)
        {
            Escrever($"FALHA no RawMouseDeltas: {ex.Message}");
        }

        Escrever("");

        var timer = new System.Windows.Forms.Timer { Interval = 20000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Finalizar();
        };
        timer.Start();
    }

    // ---------------------------------------- caminho antigo (posicao)

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == 0 && (int)wParam == WM_MOUSEMOVE)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var x = data.pt.X;
            var y = data.pt.Y;

            _eventosHook++;

            if (!_hasLast)
            {
                _lastX = x;
                _lastY = y;
                _hasLast = true;
            }
            else
            {
                var dx = x - _lastX;
                _lastX = x;
                _lastY = y;

                if (x >= SystemInformation.VirtualScreen.Right - 1)
                {
                    _eventosNaBorda++;
                    if (dx > 0) _dxPositivoNaBorda++;
                    if (dx < 0) _dxNegativoNaBorda++;

                    if (_eventosNaBorda <= 12)
                        Escrever($"  posicao  x={x,5} dx={dx,4}  NA BORDA");
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // --------------------------------------- caminho novo (raw + detector)

    private void OnRaw(int dx, int dy)
    {
        _rawEventos++;

        var vs = SystemInformation.VirtualScreen;
        var bounds = new DesktopBounds(vs.Left, vs.Top, vs.Right, vs.Bottom);
        var pos = Cursor.Position;

        if (pos.X >= vs.Right - 1)
        {
            _rawNaBorda++;
            if (_rawNaBorda <= 12) Escrever($"  raw      x={pos.X,5} dx={dx,4}  NA BORDA");
        }

        if (_detector.Feed(dx, dy, pos.X, pos.Y, bounds, Environment.TickCount64))
        {
            _travessias++;
            Escrever($"  >>> TRAVESSIA #{_travessias} disparada em x={pos.X} y={pos.Y}");
        }
    }

    private void Finalizar()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        if (_raw is not null) { _raw.Moved -= OnRaw; _raw.Dispose(); }

        Escrever("");
        Escrever("=== RESUMO ===");
        Escrever($"eventos do hook (mousemove)        : {_eventosHook}");
        Escrever($"  com o cursor na borda            : {_eventosNaBorda}");
        Escrever($"    dx > 0 (empurrando)            : {_dxPositivoNaBorda}");
        Escrever($"    dx < 0 (oscilacao para tras)   : {_dxNegativoNaBorda}");
        Escrever($"eventos de raw input               : {_rawEventos}");
        Escrever($"  com o cursor na borda            : {_rawNaBorda}");
        Escrever($"TRAVESSIAS detectadas              : {_travessias}");
        Escrever("");

        if (_rawEventos == 0)
            Escrever("VEREDITO: o raw input nao entregou nada. A correcao nao funciona aqui.");
        else if (_rawNaBorda == 0)
            Escrever("VEREDITO: o cursor nunca chegou na borda; teste inconclusivo.");
        else if (_travessias > 0)
            Escrever("VEREDITO: OK - a cadeia raw input + detector dispara a travessia. " +
                     $"A medicao por posicao teria se anulado ({_dxPositivoNaBorda} para frente " +
                     $"contra {_dxNegativoNaBorda} para tras).");
        else
            Escrever("VEREDITO: chegou na borda, raw input funcionou, mas a travessia " +
                     "nao disparou. Falta investigar o detector.");

        File.WriteAllText(_path, _log.ToString());
        Application.Exit();
    }

    private void Escrever(string line) => _log.AppendLine(line);

    // ------------------------------------------------------------- interop

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
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
