using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Properties;
using CubeKeeper.Core.Resources;
using CubeKeeper.Core.Server;
using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Process;

/// <summary>
/// Supervisa el proceso bedrock_server: arranque, parada limpia, reinicio automático con límite
/// e instalación/actualización con el servidor parado.
/// </summary>
/// <remarks>
/// Todas las operaciones de ciclo de vida (arrancar, parar, instalar) se serializan con <see cref="_lifecycle"/>.
/// La tarea que vigila la salida del proceso NO toma ese cerrojo, para que una parada pueda esperar a que
/// el proceso termine sin bloquearse; solo lo toma después, al reiniciar tras una caída.
/// </remarks>
public sealed class ServerSupervisor : IServerController, IDisposable
{
    private readonly BdsOptions _options;
    private readonly DataPaths _paths;
    private readonly IConsoleLog _console;
    private readonly IServerInstaller _installer;
    private readonly AppStateStore _state;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerSupervisor> _logger;
    private readonly ILogger _bdsLogger;
    private readonly CrashLoopGuard _guard;
    private readonly ITcpPortProbe _ports;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _stateLock = new();

    private ServerProcess? _process;
    private Task? _monitor;
    private bool _expectingExit;
    private ServerSnapshot _snapshot;
    private CancellationToken _hostStopping = CancellationToken.None;
    private bool _portRetryUsed;

    public ServerSupervisor(
        IOptions<CubeKeeperOptions> options,
        DataPaths paths,
        IConsoleLog console,
        IServerInstaller installer,
        AppStateStore state,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        ITcpPortProbe? ports = null)
    {
        _ports = ports ?? new ProcTcpPortProbe();
        _options = options.Value.Bds;
        _paths = paths;
        _console = console;
        _installer = installer;
        _state = state;
        _time = time;
        _logger = loggerFactory.CreateLogger<ServerSupervisor>();
        _bdsLogger = loggerFactory.CreateLogger("BDS");
        _guard = new CrashLoopGuard(_options.MaxRestarts, TimeSpan.FromMinutes(_options.RestartWindowMinutes));
        _snapshot = new ServerSnapshot(IdleStatus(), _installer.InstalledVersion, null, null, null, null);
    }

    public event Action<ServerSnapshot>? StateChanged;

    public event Action<BdsLogEvent>? LogEvent;

    public ServerSnapshot Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return _snapshot;
            }
        }
    }

    private bool CanLaunch => _installer.IsInstalled || !string.IsNullOrWhiteSpace(_options.LaunchCommand);

    private ServerStatus IdleStatus() => CanLaunch ? ServerStatus.Stopped : ServerStatus.NotInstalled;

    /// <summary>Se llama al iniciar el contenedor: arranca el servidor si estaba encendido la última vez.</summary>
    public async Task InitializeAsync(CancellationToken hostStopping)
    {
        _hostStopping = hostStopping;
        _paths.EnsureCreated();
        SetStatus(IdleStatus());

        if (_options.AutoStart && _state.Current.DesiredRunning && CanLaunch)
        {
            _console.Append(ConsoleLineKind.System, "El servidor estaba encendido antes de reiniciar CubeKeeper: arrancando…");
            try
            {
                await StartAsync(hostStopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "No se pudo arrancar el servidor al iniciar.");
            }
        }
    }

    /// <summary>Al parar el contenedor: parada limpia sin cambiar el estado deseado, para volver a arrancar después.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _process?.Kill();
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state.Update(s => s with { DesiredRunning = true });
            _guard.Reset();
            _portRetryUsed = false;
            await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state.Update(s => s with { DesiredRunning = false });
            await StopCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state.Update(s => s with { DesiredRunning = true });
            await StopCoreAsync(ct).ConfigureAwait(false);
            _guard.Reset();
            _portRetryUsed = false;
            await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var line = command.Trim();
        if (line.Length == 0)
        {
            return;
        }
        if (line.Contains('\n') || line.Contains('\r'))
        {
            throw new ArgumentException("Los comandos no pueden contener saltos de línea.", nameof(command));
        }

        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException("El servidor no está en marcha.");
        }
        _console.Append(ConsoleLineKind.Command, line);
        await process.WriteLineAsync(line, ct).ConfigureAwait(false);
    }

    /// <summary>Instala o actualiza BDS. Si el servidor estaba en marcha, lo para antes y lo vuelve a arrancar después.</summary>
    public async Task<InstallResult> InstallAsync(BdsRelease release, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        var wasRunning = Snapshot.Status.IsProcessAlive();
        try
        {
            if (wasRunning)
            {
                await SayAsync("El servidor se va a actualizar ahora.", ct).ConfigureAwait(false);
                await StopCoreAsync(ct).ConfigureAwait(false);
            }

            SetStatus(ServerStatus.Installing);
            _console.Append(ConsoleLineKind.System, $"Instalando BDS {release.Version} desde {release.Source}…");
            var relay = new Progress<string>(msg =>
            {
                _console.Append(ConsoleLineKind.System, msg);
                progress?.Report(msg);
            });

            InstallResult result;
            try
            {
                result = await _installer.InstallAsync(release, relay, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _console.Append(ConsoleLineKind.System, $"Error al instalar: {ex.Message}");
                SetStatus(IdleStatus(), error: ex.Message);
                throw;
            }

            _console.Append(ConsoleLineKind.System, $"BDS {result.Version} instalado.");
            if (result.AddedProperties.Count > 0)
            {
                _console.Append(ConsoleLineKind.System,
                    $"Ajustes nuevos añadidos a server.properties: {string.Join(", ", result.AddedProperties)}");
            }
            SetStatus(IdleStatus());

            if (wasRunning || _state.Current.DesiredRunning)
            {
                _guard.Reset();
                _portRetryUsed = false;
                await StartCoreAsync(ct).ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task SayAsync(string message, CancellationToken ct)
    {
        try
        {
            await SendCommandAsync($"say {message}", ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Si no está en marcha, no hay nadie a quien avisar.
        }
    }

    /// <summary>
    /// Espera a que el puerto TCP del servidor esté libre y lanza el proceso. Debe llamarse con
    /// <see cref="_lifecycle"/> tomado.
    /// </summary>
    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false })
        {
            return;
        }
        if (CanLaunch && ChecksPort)
        {
            await WaitForPortFreeAsync(ct).ConfigureAwait(false);
        }
        StartCore();
    }

    /// <summary>Solo con el bedrock_server real (en las pruebas se lanza un servidor falso que no abre el puerto).</summary>
    private bool ChecksPort => _options.PortCheckSeconds > 0 &&
                               (string.IsNullOrWhiteSpace(_options.LaunchCommand) || _options.PortCheckWithLaunchCommand);

    private async Task<int> ServerPortAsync(CancellationToken ct)
    {
        try
        {
            var doc = await ServerPropertiesDocument.LoadAsync(_paths.ServerProperties, ct).ConfigureAwait(false);
            return doc.GetInt("server-port") is int p and > 0 and < 65536 ? p : 19132;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 19132;
        }
    }

    /// <summary>
    /// BDS abre el puerto TCP sin SO_REUSEADDR: si quedan conexiones recientes (TIME_WAIT, unos 60 s en Linux), no
    /// puede abrirlo y arranca sin que nadie pueda entrar. Se espera a que se liberen (como mucho 2 minutos).
    /// </summary>
    private async Task WaitForPortFreeAsync(CancellationToken ct)
    {
        var port = await ServerPortAsync(ct).ConfigureAwait(false);
        var deadline = _time.GetUtcNow().AddMinutes(2);
        var announced = false;
        while (true)
        {
            var states = _ports.States(port);
            if (states is null || TcpSockets.Busy(states) == 0)
            {
                if (announced)
                {
                    _console.Append(ConsoleLineKind.System, $"Puerto TCP {port} libre.");
                }
                return;
            }
            if (_time.GetUtcNow() >= deadline)
            {
                _console.Append(ConsoleLineKind.System,
                    $"El puerto TCP {port} sigue ocupado tras 2 minutos; se arranca igualmente (puede que no se pueda entrar).");
                _logger.LogWarning("Puerto TCP {Port} ocupado al arrancar BDS", port);
                return;
            }
            if (!announced)
            {
                announced = true;
                SetStatus(ServerStatus.Starting, runningVersion: null, startedAt: _time.GetLocalNow(), error: null);
                _console.Append(ConsoleLineKind.System,
                    $"Esperando a que se libere el puerto TCP {port} ({TcpSockets.Busy(states)} conexiones recientes, por " +
                    "ejemplo de MCXboxBroadcast): si BDS arranca con él ocupado, nadie puede entrar. Suele tardar menos de un minuto.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tras «Server started», comprueba que BDS escucha en el puerto TCP. Si no, lo reinicia una vez (esperando a que
    /// el puerto se libere); si sigue sin abrirlo, lo deja en marcha y lo dice en la consola.
    /// </summary>
    private async Task VerifyListeningAsync(ServerProcess process)
    {
        try
        {
            var port = await ServerPortAsync(_hostStopping).ConfigureAwait(false);
            var deadline = _time.GetUtcNow().AddSeconds(_options.PortCheckSeconds);
            while (_time.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _time, _hostStopping).ConfigureAwait(false);
                if (!ReferenceEquals(_process, process) || process.HasExited)
                {
                    return;
                }
                var states = _ports.States(port);
                if (states is null || TcpSockets.IsListening(states))
                {
                    return;
                }
            }

            _logger.LogWarning("BDS no escucha en TCP {Port} tras arrancar", port);
            await _lifecycle.WaitAsync(_hostStopping).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_process, process) || process.HasExited)
                {
                    return;
                }
                if (_portRetryUsed)
                {
                    var msg = $"BDS sigue sin abrir el puerto TCP {port}: no se podrá entrar. Comprueba que ningún otro " +
                              "programa lo usa y reinicia el servidor.";
                    _console.Append(ConsoleLineKind.System, msg);
                    lock (_stateLock)
                    {
                        _snapshot = _snapshot with { LastError = msg };
                    }
                    RaiseStateChanged();
                    return;
                }
                _portRetryUsed = true;
                _console.Append(ConsoleLineKind.System,
                    $"BDS ha arrancado pero no ha abierto el puerto TCP {port} (estaba ocupado por conexiones recientes). " +
                    "Se reinicia para que lo abra…");
                await StopCoreAsync(_hostStopping).ConfigureAwait(false);
                await StartCoreAsync(_hostStopping).ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Apagando.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al comprobar el puerto de BDS");
        }
    }

    /// <summary>Lanza el proceso. Debe llamarse con <see cref="_lifecycle"/> tomado.</summary>
    private void StartCore()
    {
        if (_process is { HasExited: false })
        {
            return;
        }
        if (!CanLaunch)
        {
            SetStatus(ServerStatus.NotInstalled);
            throw new InvalidOperationException("No hay ningún servidor instalado. Instálalo primero desde el panel.");
        }

        var fileName = string.IsNullOrWhiteSpace(_options.LaunchCommand) ? _paths.ServerExecutable : _options.LaunchCommand!;
        var arguments = string.IsNullOrWhiteSpace(_options.LaunchCommand) ? null : _options.LaunchArguments;

        _expectingExit = false;
        SetStatus(ServerStatus.Starting, runningVersion: null, startedAt: _time.GetLocalNow(), error: null);
        _console.Append(ConsoleLineKind.System, "Arrancando bedrock_server…");

        ServerProcess process;
        try
        {
            process = ServerProcess.Start(fileName, arguments, _paths.Server, OnOutput, OnError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo lanzar {File}", fileName);
            _console.Append(ConsoleLineKind.System, $"No se pudo lanzar el servidor: {ex.Message}");
            SetStatus(ServerStatus.Failed, error: ex.Message);
            throw new InvalidOperationException($"No se pudo lanzar el servidor: {ex.Message}", ex);
        }

        _process = process;
        lock (_stateLock)
        {
            _snapshot = _snapshot with { ProcessId = process.Id };
        }
        _logger.LogInformation("bedrock_server lanzado (PID {Pid})", process.Id);
        _monitor = Task.Run(() => MonitorAsync(process));
    }

    /// <summary>Para el proceso de forma limpia. Debe llamarse con <see cref="_lifecycle"/> tomado.</summary>
    private async Task StopCoreAsync(CancellationToken ct)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            if (_monitor is not null)
            {
                await _monitor.ConfigureAwait(false);
            }
            SetStatus(IdleStatus());
            return;
        }

        _expectingExit = true;
        SetStatus(ServerStatus.Stopping);
        _console.Append(ConsoleLineKind.System, "Parando el servidor (stop)…");
        try
        {
            await process.WriteLineAsync("stop", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "No se pudo enviar 'stop'; se forzará la parada.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.StopTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _console.Append(ConsoleLineKind.System,
                $"El servidor no terminó en {_options.StopTimeoutSeconds} s: se fuerza la parada.");
            process.Kill();
        }

        if (_monitor is not null)
        {
            await _monitor.ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(ServerProcess process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error esperando al proceso");
        }

        var exitCode = SafeExitCode(process);
        var expected = _expectingExit;
        if (ReferenceEquals(_process, process))
        {
            _process = null;
        }
        process.Dispose();

        if (expected)
        {
            _console.Append(ConsoleLineKind.System, $"Servidor parado (código {exitCode}).");
            SetStatus(IdleStatus(), runningVersion: null, startedAt: null);
            return;
        }

        _logger.LogWarning("bedrock_server terminó inesperadamente con código {Code}", exitCode);
        _console.Append(ConsoleLineKind.System, $"El servidor se ha cerrado inesperadamente (código {exitCode}).");

        if (_hostStopping.IsCancellationRequested)
        {
            SetStatus(IdleStatus());
            return;
        }

        if (!_guard.TryRegisterRestart(_time.GetUtcNow()))
        {
            var msg = $"Se ha caído más de {_options.MaxRestarts} veces en {_options.RestartWindowMinutes} min. " +
                      "No se reintenta: revisa la consola y arráncalo a mano.";
            _console.Append(ConsoleLineKind.System, msg);
            SetStatus(ServerStatus.Failed, runningVersion: null, startedAt: null, error: msg);
            return;
        }

        SetStatus(ServerStatus.Crashed, runningVersion: null, startedAt: null, error: $"Código de salida {exitCode}");
        _console.Append(ConsoleLineKind.System, $"Reintentando en {_options.RestartDelaySeconds} s…");
        _ = Task.Run(RestartAfterCrashAsync);
    }

    private async Task RestartAfterCrashAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.RestartDelaySeconds), _time, _hostStopping).ConfigureAwait(false);
            await _lifecycle.WaitAsync(_hostStopping).ConfigureAwait(false);
            try
            {
                // Puede que entretanto alguien lo haya parado o arrancado a mano.
                if (Snapshot.Status == ServerStatus.Crashed && _state.Current.DesiredRunning)
                {
                    await StartCoreAsync(_hostStopping).ConfigureAwait(false);
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Apagando.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al reiniciar tras una caída");
        }
    }

    private void OnOutput(string line)
    {
        _bdsLogger.LogInformation("{Line}", line);
        _console.Append(ConsoleLineKind.Output, line);

        var evt = BdsLogParser.Parse(line);
        switch (evt)
        {
            case ServerStarted:
                SetStatus(ServerStatus.Running);
                if (ChecksPort && _process is { } started)
                {
                    _ = Task.Run(() => VerifyListeningAsync(started));
                }
                break;
            case ServerVersionReported v:
                lock (_stateLock)
                {
                    _snapshot = _snapshot with { RunningVersion = v.Version };
                }
                RaiseStateChanged();
                break;
        }

        if (evt is not null)
        {
            try
            {
                LogEvent?.Invoke(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en un suscriptor de eventos del log");
            }
        }
    }

    private void OnError(string line)
    {
        _bdsLogger.LogWarning("{Line}", line);
        _console.Append(ConsoleLineKind.Error, line);
    }

    private static int SafeExitCode(ServerProcess p)
    {
        try
        {
            return p.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private void SetStatus(
        ServerStatus status,
        Optional<string?> runningVersion = default,
        Optional<DateTimeOffset?> startedAt = default,
        Optional<string?> error = default)
    {
        lock (_stateLock)
        {
            _snapshot = _snapshot with
            {
                Status = status,
                InstalledVersion = _installer.InstalledVersion,
                RunningVersion = runningVersion.HasValue ? runningVersion.Value : _snapshot.RunningVersion,
                StartedAt = startedAt.HasValue ? startedAt.Value : _snapshot.StartedAt,
                LastError = error.HasValue ? error.Value : _snapshot.LastError,
                ProcessId = status.IsProcessAlive() ? _snapshot.ProcessId : null,
            };
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        var snap = Snapshot;
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }
        foreach (var h in handlers.GetInvocationList().Cast<Action<ServerSnapshot>>())
        {
            try
            {
                h(snap);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Suscriptor de estado con error");
            }
        }
    }

    public void Dispose()
    {
        _process?.Kill();
        _process?.Dispose();
        _lifecycle.Dispose();
    }

    /// <summary>Distingue "no cambiar" de "poner a null" en <see cref="SetStatus"/>.</summary>
    private readonly struct Optional<T>
    {
        public Optional(T value)
        {
            Value = value;
            HasValue = true;
        }

        public T Value { get; }

        public bool HasValue { get; }

        public static implicit operator Optional<T>(T value) => new(value);
    }
}
