namespace RemoteMirror.Core;

/// <summary>
/// Garante uma instancia por modo e permite pedir que a instancia atual saia.
///
/// Por que nao basta matar o processo: host e modo terceiro monitor sao o
/// mesmo executavel, e quando o programa e aberto pela tela de escolha o modo
/// nao aparece na linha de comando. Filtrar por nome de processo derruba o
/// modo errado; filtrar por argumento deixa passar justamente quem foi aberto
/// pela tela de escolha. Com duas instancias do terceiro monitor vivas, os
/// dois hooks de mouse disputam a mesma entrada - e e assim que alguem fica
/// sem cursor.
///
/// Aqui cada modo tem um nome proprio. Quem chega pede ao que esta rodando
/// para sair, e espera a vaga.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _quitSignal;
    private readonly bool _owned;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle quitSignal, bool owned)
    {
        _mutex = mutex;
        _quitSignal = quitSignal;
        _owned = owned;
    }

    /// <summary>true se este processo e o dono do modo.</summary>
    public bool IsOwner => _owned;

    /// <summary>
    /// Tenta assumir o modo.
    /// </summary>
    /// <param name="mode">Nome curto do modo, por exemplo "share" ou "host".</param>
    /// <param name="waitForVacancy">
    /// Quanto esperar a instancia anterior sair, quando ela foi avisada.
    /// </param>
    public static SingleInstance TryAcquire(string mode, TimeSpan waitForVacancy = default)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName(mode));
        var quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitName(mode));

        var owned = Wait(mutex, TimeSpan.Zero);

        if (!owned && waitForVacancy > TimeSpan.Zero)
            owned = Wait(mutex, waitForVacancy);

        // Limpa o sinal: ele e manual, e um pedido antigo faria esta
        // instancia sair logo depois de assumir.
        if (owned) quit.Reset();

        return new SingleInstance(mutex, quit, owned);
    }

    private static bool Wait(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // A instancia anterior morreu sem liberar: a vaga e nossa.
            return true;
        }
    }

    /// <summary>
    /// Pede para a instancia que estiver rodando aquele modo sair, sem mata-la:
    /// ela desfaz os hooks e devolve o cursor antes de encerrar.
    /// </summary>
    /// <returns>true se havia alguem para avisar.</returns>
    public static bool RequestQuit(string mode)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(QuitName(mode), out var handle)) return false;

            using (handle) handle.Set();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Espera o modo ficar livre e devolve a vaga em seguida.
    ///
    /// A instalacao precisa disto antes de copiar o executavel: pedir a saida
    /// nao e o mesmo que a saida ter acontecido, e copiar por cima de um
    /// arquivo ainda em uso falha. Era uma corrida que aparecia so as vezes -
    /// o pior tipo.
    /// </summary>
    /// <returns>false se ainda havia alguem no fim do prazo.</returns>
    public static bool WaitForVacancy(string mode, TimeSpan timeout)
    {
        using var probe = TryAcquire(mode, timeout);
        return probe.IsOwner;
    }

    /// <summary>
    /// Chama <paramref name="onQuit"/> quando alguem pedir a saida deste modo.
    /// A espera fica numa thread de fundo, que nao impede o processo de sair.
    /// </summary>
    public void OnQuitRequested(Action onQuit)
    {
        var thread = new Thread(() =>
        {
            try
            {
                _quitSignal.WaitOne();
                if (!_disposed) onQuit();
            }
            catch { /* processo encerrando */ }
        })
        {
            IsBackground = true,
            Name = "RemoteMirror quit watcher",
        };

        thread.Start();
    }

    private static string MutexName(string mode) => $@"Local\RemoteMirror.{mode}.instance";

    private static string QuitName(string mode) => $@"Local\RemoteMirror.{mode}.quit";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_owned) _mutex.ReleaseMutex();
        }
        catch { /* nunca foi nosso */ }

        _mutex.Dispose();
        _quitSignal.Dispose();
    }
}
