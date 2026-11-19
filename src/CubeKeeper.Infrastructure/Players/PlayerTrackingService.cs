using System.Threading.Channels;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Players;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Players;

/// <summary>
/// Sigue la salida de BDS para saber quién está conectado y guarda cada sesión en SQLite.
/// Todo pasa por una cola con un único consumidor, así que el <see cref="PlayerTracker"/> no necesita cerrojos
/// y el hilo que lee stdout nunca espera a la base de datos.
/// </summary>
public sealed class PlayerTrackingService : BackgroundService, IPlayerPresence
{
    /// <summary>Cada cuánto se envía <c>list</c> mientras haya alguien conectado, por si se perdió una salida.</summary>
    public static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(10);

    private readonly IServerController _server;
    private readonly IConsoleLog _console;
    private readonly IDbContextFactory<CubeKeeperDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<PlayerTrackingService> _logger;
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, string> _xuidByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlayerTracker _tracker;
    private volatile IReadOnlyList<OnlinePlayer> _online = [];
    private ServerStatus _lastStatus;

    public PlayerTrackingService(
        IServerController server,
        IConsoleLog console,
        IDbContextFactory<CubeKeeperDbContext> dbFactory,
        TimeProvider time,
        ILogger<PlayerTrackingService> logger)
    {
        _server = server;
        _console = console;
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
        _tracker = new PlayerTracker(name => _xuidByName.GetValueOrDefault(name));
        _lastStatus = server.Snapshot.Status;
    }

    public IReadOnlyList<OnlinePlayer> Online => _online;

    public event Action? Changed;

    public Task ResyncAsync(CancellationToken ct = default) => _server.SendCommandAsync("list", ct);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await CloseDanglingSessionsAsync(cancellationToken).ConfigureAwait(false);
        _console.LineAdded += OnLine;
        _server.StateChanged += OnState;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _console.LineAdded -= OnLine;
        _server.StateChanged -= OnState;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ResyncInterval, _time);
        var tick = TickAsync(timer, stoppingToken);

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var now = _time.GetUtcNow();
                var changes = item switch
                {
                    LineItem l => _tracker.Process(l.Line, now),
                    StoppedItem => _tracker.ServerStopped(now),
                    _ => [],
                };
                if (changes.Count == 0)
                {
                    continue;
                }

                _online = _tracker.Online;
                try
                {
                    await PersistAsync(changes, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "No se pudo guardar el historial de jugadores");
                }
                RaiseChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado.
        }

        // Al apagar CubeKeeper con gente dentro, cerrar sus sesiones.
        var remaining = _tracker.ServerStopped(_time.GetUtcNow());
        if (remaining.Count > 0)
        {
            try
            {
                await PersistAsync(remaining, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron cerrar las sesiones al apagar");
            }
        }
        await tick.ConfigureAwait(false);
    }

    private async Task TickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_online.Count > 0 && _server.Snapshot.Status == ServerStatus.Running)
                {
                    try
                    {
                        await ResyncAsync(ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // Se acaba de parar.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnLine(ConsoleLine line)
    {
        if (line.Kind == ConsoleLineKind.Output)
        {
            _queue.Writer.TryWrite(new LineItem(BdsLogLine.Parse(line.Text)));
        }
    }

    private void OnState(ServerSnapshot snap)
    {
        var wasAlive = _lastStatus is ServerStatus.Starting or ServerStatus.Running;
        _lastStatus = snap.Status;
        if (wasAlive && snap.Status is not (ServerStatus.Starting or ServerStatus.Running))
        {
            _queue.Writer.TryWrite(new StoppedItem());
        }
    }

    private async Task PersistAsync(IReadOnlyList<PlayerChange> changes, CancellationToken ct)
    {
        foreach (var change in changes)
        {
            if (change.Player.Xuid is null)
            {
                // Solo visto en "list" y sin XUID conocido: no se guarda.
                continue;
            }
            // El servicio de acceso también puede crear la ficha del jugador a la vez (al bloquearlo): si choca con
            // la clave única, se reintenta una vez con un contexto nuevo.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await PersistOneAsync(change, ct).ConfigureAwait(false);
                    break;
                }
                catch (DbUpdateException) when (attempt == 1)
                {
                }
            }
        }
    }

    private async Task PersistOneAsync(PlayerChange change, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var p = change.Player;
        var xuid = p.Xuid!;
        var player = await db.Players.FindAsync([xuid], ct).ConfigureAwait(false);
        if (player is null)
        {
            player = new Player { Xuid = xuid, Name = p.Name, FirstSeen = change.At, LastSeen = change.At };
            db.Players.Add(player);
        }
        player.Name = p.Name;
        player.LastSeen = change.At;
        _xuidByName[p.Name] = xuid;

        switch (change)
        {
            case PlayerJoined:
                db.PlayerSessions.Add(new PlayerSession { Xuid = xuid, Name = p.Name, ConnectedAt = change.At });
                _logger.LogInformation("Entra {Name} ({Xuid})", p.Name, xuid);
                break;
            case PlayerLeft left:
                var open = (await db.PlayerSessions
                        .Where(s => s.Xuid == xuid && s.DisconnectedAt == null)
                        .ToListAsync(ct).ConfigureAwait(false))
                    .OrderByDescending(s => s.ConnectedAt)
                    .FirstOrDefault();
                if (open is not null)
                {
                    open.DisconnectedAt = change.At;
                    open.EndReason = SessionEndReasonMapping.From(left.Reason);
                }
                _logger.LogInformation("Sale {Name} ({Xuid}): {Reason}", p.Name, xuid, left.Reason);
                break;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Si CubeKeeper se cerró de golpe, quedan sesiones abiertas: se cierran con la última hora conocida.</summary>
    private async Task CloseDanglingSessionsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            foreach (var p in await db.Players.ToListAsync(ct).ConfigureAwait(false))
            {
                _xuidByName[p.Name] = p.Xuid;
            }

            var open = await db.PlayerSessions.Include(s => s.Player).Where(s => s.DisconnectedAt == null).ToListAsync(ct).ConfigureAwait(false);
            foreach (var s in open)
            {
                var end = s.Player?.LastSeen is { } seen && seen > s.ConnectedAt ? seen : s.ConnectedAt;
                s.DisconnectedAt = end;
                s.EndReason = SessionEndReason.Interrupted;
            }
            if (open.Count > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("{Count} sesiones abiertas de un apagado anterior se han cerrado como interrumpidas", open.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "No se pudo revisar el historial de jugadores al arrancar");
        }
    }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }
        foreach (var h in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                h();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Suscriptor de jugadores con error");
            }
        }
    }

    private abstract record Item;

    private sealed record LineItem(BdsLogLine Line) : Item;

    private sealed record StoppedItem : Item;
}
