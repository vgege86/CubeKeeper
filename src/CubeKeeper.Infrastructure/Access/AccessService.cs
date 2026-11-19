using System.Text;
using System.Threading.Channels;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Access;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Properties;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Access;

public sealed record ApprovedPlayer(string Name, string Xuid, PermissionLevel? Permission, bool Online);

public sealed record KnownPlayer(string Name, string Xuid, DateTimeOffset LastSeen, bool Online);

public sealed record AccessOverview(
    AccessMode Mode,
    DateTimeOffset? EnrollmentUntil,
    IReadOnlyList<AccessRequest> Pending,
    IReadOnlyList<ApprovedPlayer> Approved,
    IReadOnlyList<KnownPlayer> NotApproved,
    IReadOnlyList<KnownPlayer> Blocked,
    IReadOnlyList<AllowListEntry> NameOnlyEntries);

/// <summary>
/// Control de acceso en dos capas:
/// <list type="number">
/// <item>BDS: <c>allow-list</c> en server.properties y <c>allowlist.json</c> por XUID, recargado con <c>allowlist reload</c>.</item>
/// <item>CubeKeeper: al conectarse alguien se decide con <see cref="AccessPolicy"/> si se le deja, se le expulsa
/// o (en modo inscripción) se guarda su solicitud y se le expulsa. Así funciona también sin reiniciar y para los
/// bloqueados, que Bedrock no sabe banear.</item>
/// </list>
/// En modo inscripción se envía <c>allowlist off</c> durante N minutos para que cualquiera pueda llegar a conectarse
/// (y así conocer su XUID); al acabar se vuelve a <c>allowlist on</c>.
/// </summary>
public sealed class AccessService : BackgroundService
{
    public const string BlockedMessage = "Estás bloqueado en este servidor.";
    public const string RequestMessage = "Solicitud enviada. Cuando te aprueben, vuelve a entrar.";
    public const string NotApprovedMessage = "No estás en la lista de este servidor. Pide que te aprueben.";

    private readonly IServerController _server;
    private readonly IPlayerPresence _presence;
    private readonly DataPaths _paths;
    private readonly AppStateStore _state;
    private readonly IDbContextFactory<CubeKeeperDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<AccessService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<BdsLogEvent> _joins = Channel.CreateUnbounded<BdsLogEvent>(new UnboundedChannelOptions { SingleReader = true });
    private HashSet<string> _approved = new(StringComparer.Ordinal);
    private HashSet<string> _blocked = new(StringComparer.Ordinal);
    private ServerStatus _lastStatus;

    public AccessService(
        IServerController server,
        IPlayerPresence presence,
        DataPaths paths,
        AppStateStore state,
        IDbContextFactory<CubeKeeperDbContext> dbFactory,
        TimeProvider time,
        ILogger<AccessService> logger)
    {
        _server = server;
        _presence = presence;
        _paths = paths;
        _state = state;
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
        _lastStatus = server.Snapshot.Status;
    }

    public event Action? Changed;

    public string AllowListPath => Path.Combine(_paths.Server, "allowlist.json");

    public string PermissionsPath => Path.Combine(_paths.Server, "permissions.json");

    public AccessMode Mode => _state.Current.AccessMode;

    public DateTimeOffset? EnrollmentUntil => _state.Current.EnrollmentUntil is { } until && until > _time.GetUtcNow() ? until : null;

    public bool EnrollmentActive => EnrollmentUntil is not null;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReloadCachesAsync(cancellationToken).ConfigureAwait(false);
        _server.LogEvent += OnLogEvent;
        _server.StateChanged += OnState;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _server.LogEvent -= OnLogEvent;
        _server.StateChanged -= OnState;
        _joins.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var expiry = WatchEnrollmentAsync(stoppingToken);
        try
        {
            await foreach (var evt in _joins.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleJoinAsync(evt, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error aplicando el control de acceso");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        await expiry.ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Consultas

    public async Task<AccessOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var allow = await LoadAllowListAsync(ct).ConfigureAwait(false);
        var perms = await LoadPermissionsAsync(ct).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var players = await db.Players.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var pending = (await db.AccessRequests.AsNoTracking().Where(r => r.Status == AccessRequestStatus.Pending).ToListAsync(ct).ConfigureAwait(false))
            .OrderByDescending(r => r.RequestedAt).ToList();
        var online = _presence.Online.Where(o => o.Xuid is not null).Select(o => o.Xuid!).ToHashSet(StringComparer.Ordinal);

        var approved = allow.Entries.Where(e => e.Xuid is not null)
            .Select(e => new ApprovedPlayer(e.Name, e.Xuid!, perms.Get(e.Xuid!), online.Contains(e.Xuid!)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var approvedSet = approved.Select(a => a.Xuid).ToHashSet(StringComparer.Ordinal);
        var pendingSet = pending.Select(r => r.Xuid).ToHashSet(StringComparer.Ordinal);

        return new AccessOverview(
            Mode,
            EnrollmentUntil,
            pending,
            approved,
            players.Where(p => !p.Blocked && !approvedSet.Contains(p.Xuid) && !pendingSet.Contains(p.Xuid))
                .Select(p => new KnownPlayer(p.Name, p.Xuid, p.LastSeen, online.Contains(p.Xuid)))
                .OrderByDescending(p => p.LastSeen).ToList(),
            players.Where(p => p.Blocked)
                .Select(p => new KnownPlayer(p.Name, p.Xuid, p.BlockedAt ?? p.LastSeen, online.Contains(p.Xuid)))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            allow.Entries.Where(e => e.Xuid is null).ToList());
    }

    // ---------------------------------------------------------------- Acciones

    /// <summary>Añade a la lista de permitidos con el permiso indicado y marca sus solicitudes como aprobadas.</summary>
    public async Task ApproveAsync(string xuid, string name, PermissionLevel permission, CancellationToken ct = default)
    {
        ValidateXuid(xuid);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var allow = await LoadAllowListAsync(ct).ConfigureAwait(false);
            allow.Add(name, xuid);
            await SaveAsync(AllowListPath, allow.ToJson(), ct).ConfigureAwait(false);
            var perms = await LoadPermissionsAsync(ct).ConfigureAwait(false);
            perms.Set(xuid, permission);
            await SaveAsync(PermissionsPath, perms.ToJson(), ct).ConfigureAwait(false);

            await WithRetryAsync(async db =>
            {
                await SetBlockedAsync(db, xuid, name, false, ct).ConfigureAwait(false);
                foreach (var r in await db.AccessRequests.Where(r => r.Xuid == xuid && r.Status == AccessRequestStatus.Pending).ToListAsync(ct).ConfigureAwait(false))
                {
                    r.Status = AccessRequestStatus.Approved;
                    r.DecidedAt = _time.GetUtcNow();
                }
            }, ct).ConfigureAwait(false);
            _logger.LogInformation("Aprobado {Name} ({Xuid}) como {Permission}", name, xuid, permission);
        }
        finally
        {
            _gate.Release();
        }
        await AfterChangeAsync(ct).ConfigureAwait(false);
        await ApplyLivePermissionAsync(xuid, name, permission, ct).ConfigureAwait(false);
    }

    public async Task RejectAsync(long requestId, CancellationToken ct = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var r = await db.AccessRequests.FindAsync([requestId], ct).ConfigureAwait(false);
            if (r is null)
            {
                return;
            }
            r.Status = AccessRequestStatus.Rejected;
            r.DecidedAt = _time.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        RaiseChanged();
    }

    /// <summary>Quita de la lista de permitidos. En modo «solo aprobados», si está dentro, se le expulsa.</summary>
    public async Task RevokeAsync(string xuid, CancellationToken ct = default)
    {
        ValidateXuid(xuid);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var allow = await LoadAllowListAsync(ct).ConfigureAwait(false);
            if (allow.Remove(xuid))
            {
                await SaveAsync(AllowListPath, allow.ToJson(), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
        await AfterChangeAsync(ct).ConfigureAwait(false);
        if (Mode == AccessMode.ApprovedOnly)
        {
            await KickIfOnlineAsync(xuid, NotApprovedMessage, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Bloquear = quitar de la lista de permitidos + expulsar ahora y en cada intento futuro.</summary>
    public async Task BlockAsync(string xuid, string name, CancellationToken ct = default)
    {
        ValidateXuid(xuid);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var allow = await LoadAllowListAsync(ct).ConfigureAwait(false);
            if (allow.Remove(xuid))
            {
                await SaveAsync(AllowListPath, allow.ToJson(), ct).ConfigureAwait(false);
            }
            await WithRetryAsync(async db =>
            {
                await SetBlockedAsync(db, xuid, name, true, ct).ConfigureAwait(false);
                foreach (var r in await db.AccessRequests.Where(r => r.Xuid == xuid && r.Status == AccessRequestStatus.Pending).ToListAsync(ct).ConfigureAwait(false))
                {
                    r.Status = AccessRequestStatus.Rejected;
                    r.DecidedAt = _time.GetUtcNow();
                }
            }, ct).ConfigureAwait(false);
            _logger.LogInformation("Bloqueado {Name} ({Xuid})", name, xuid);
        }
        finally
        {
            _gate.Release();
        }
        await AfterChangeAsync(ct).ConfigureAwait(false);
        await KickIfOnlineAsync(xuid, BlockedMessage, ct).ConfigureAwait(false);
    }

    public async Task UnblockAsync(string xuid, CancellationToken ct = default)
    {
        ValidateXuid(xuid);
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await SetBlockedAsync(db, xuid, null, false, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        await AfterChangeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cambia el permiso (permissions.json). Si está conectado, se aplica al momento con op/deop.</summary>
    public async Task SetPermissionAsync(string xuid, string name, PermissionLevel permission, CancellationToken ct = default)
    {
        ValidateXuid(xuid);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var perms = await LoadPermissionsAsync(ct).ConfigureAwait(false);
            if (perms.Set(xuid, permission))
            {
                await SaveAsync(PermissionsPath, perms.ToJson(), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
        await AfterChangeAsync(ct).ConfigureAwait(false);
        await ApplyLivePermissionAsync(xuid, name, permission, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cambia el modo. «Solo aprobados» pone allow-list=true en server.properties (para los próximos arranques),
    /// envía <c>allowlist on</c> y expulsa a los conectados que no estén aprobados.
    /// </summary>
    public async Task SetModeAsync(AccessMode mode, CancellationToken ct = default)
    {
        _state.Update(s => s with { AccessMode = mode, EnrollmentUntil = mode == AccessMode.Open ? null : s.EnrollmentUntil });
        await WriteAllowListPropertyAsync(mode == AccessMode.ApprovedOnly, ct).ConfigureAwait(false);
        await TryCommandAsync(mode == AccessMode.ApprovedOnly && !EnrollmentActive ? "allowlist on" : "allowlist off", ct).ConfigureAwait(false);

        if (mode == AccessMode.ApprovedOnly && !EnrollmentActive)
        {
            foreach (var p in _presence.Online)
            {
                if (p.Xuid is null || !_approved.Contains(p.Xuid))
                {
                    await KickAsync(p.Name, NotApprovedMessage, ct).ConfigureAwait(false);
                }
            }
        }
        _logger.LogInformation("Modo de acceso: {Mode}", mode);
        RaiseChanged();
    }

    /// <summary>Modo inscripción: durante <paramref name="minutes"/> minutos, quien entre sin estar aprobado deja una solicitud.</summary>
    public async Task StartEnrollmentAsync(int minutes, CancellationToken ct = default)
    {
        if (minutes is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "Entre 1 y 120 minutos.");
        }
        if (Mode != AccessMode.ApprovedOnly)
        {
            throw new InvalidOperationException("El modo inscripción solo tiene sentido con «Solo aprobados».");
        }
        var until = _time.GetUtcNow().AddMinutes(minutes);
        _state.Update(s => s with { EnrollmentUntil = until });
        await TryCommandAsync("allowlist off", ct).ConfigureAwait(false);
        _logger.LogInformation("Modo inscripción hasta {Until}", until);
        RaiseChanged();
    }

    public async Task StopEnrollmentAsync(CancellationToken ct = default)
    {
        _state.Update(s => s with { EnrollmentUntil = null });
        if (Mode == AccessMode.ApprovedOnly)
        {
            await TryCommandAsync("allowlist on", ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Fin del modo inscripción");
        RaiseChanged();
    }

    public async Task KickAsync(string name, string reason, CancellationToken ct = default)
    {
        var safeReason = AccessPolicy.SafeText(reason);
        await TryCommandAsync($"kick {AccessPolicy.QuoteName(name)} {safeReason}".TrimEnd(), ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Internos

    private void OnLogEvent(BdsLogEvent evt)
    {
        if (evt is PlayerConnected or PlayerSpawned)
        {
            _joins.Writer.TryWrite(evt);
        }
    }

    private void OnState(ServerSnapshot snap)
    {
        var becameRunning = _lastStatus != ServerStatus.Running && snap.Status == ServerStatus.Running;
        _lastStatus = snap.Status;
        if (becameRunning && EnrollmentActive)
        {
            // Tras un reinicio, BDS vuelve a leer allow-list=true: reabrir la inscripción.
            _ = TryCommandAsync("allowlist off", CancellationToken.None);
        }
    }

    private async Task HandleJoinAsync(BdsLogEvent evt, CancellationToken ct)
    {
        var (name, xuid) = evt switch
        {
            PlayerConnected c => (c.Name, c.Xuid),
            PlayerSpawned s => (s.Name, s.Xuid),
            _ => (string.Empty, string.Empty),
        };
        var action = AccessPolicy.Decide(string.IsNullOrEmpty(xuid) ? null : xuid, Mode, EnrollmentActive,
            _approved.Contains, _blocked.Contains);
        if (action == JoinAction.Allow)
        {
            return;
        }

        // Se expulsa tanto en "Player connected" como en "Player Spawned": si la primera llega antes de que BDS
        // tenga al jugador del todo, la segunda lo consigue. Una expulsión de más solo da "No targets matched".
        switch (action)
        {
            case JoinAction.KickBlocked:
                await KickAsync(name, BlockedMessage, ct).ConfigureAwait(false);
                break;
            case JoinAction.RecordRequestAndKick:
                if (evt is PlayerConnected)
                {
                    await RecordRequestAsync(name, xuid, ct).ConfigureAwait(false);
                }
                await KickAsync(name, RequestMessage, ct).ConfigureAwait(false);
                break;
            case JoinAction.KickNotApproved:
                await KickAsync(name, NotApprovedMessage, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task RecordRequestAsync(string name, string xuid, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.AccessRequests.FirstOrDefaultAsync(r => r.Xuid == xuid && r.Status == AccessRequestStatus.Pending, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.AccessRequests.Add(new AccessRequest { Xuid = xuid, Name = name, RequestedAt = _time.GetUtcNow() });
        }
        else
        {
            existing.Name = name;
            existing.Attempts++;
            existing.RequestedAt = _time.GetUtcNow();
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Solicitud de acceso de {Name} ({Xuid})", name, xuid);
        RaiseChanged();
    }

    private async Task WatchEnrollmentAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_state.Current.EnrollmentUntil is { } until && until <= _time.GetUtcNow())
                {
                    await StopEnrollmentAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task KickIfOnlineAsync(string xuid, string reason, CancellationToken ct)
    {
        foreach (var p in _presence.Online.Where(p => p.Xuid == xuid))
        {
            await KickAsync(p.Name, reason, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyLivePermissionAsync(string xuid, string name, PermissionLevel permission, CancellationToken ct)
    {
        var online = _presence.Online.FirstOrDefault(p => p.Xuid == xuid);
        if (online is null)
        {
            return;
        }
        await TryCommandAsync(permission == PermissionLevel.Operator
            ? $"op {AccessPolicy.QuoteName(online.Name)}"
            : $"deop {AccessPolicy.QuoteName(online.Name)}", ct).ConfigureAwait(false);
    }

    /// <summary>Recarga cachés y, con el servidor en marcha, pide a BDS que relea los archivos.</summary>
    private async Task AfterChangeAsync(CancellationToken ct)
    {
        await ReloadCachesAsync(ct).ConfigureAwait(false);
        await TryCommandAsync("allowlist reload", ct).ConfigureAwait(false);
        await TryCommandAsync("permission reload", ct).ConfigureAwait(false);
        RaiseChanged();
    }

    private async Task ReloadCachesAsync(CancellationToken ct)
    {
        try
        {
            var allow = await LoadAllowListAsync(ct).ConfigureAwait(false);
            _approved = allow.Entries.Where(e => e.Xuid is not null).Select(e => e.Xuid!).ToHashSet(StringComparer.Ordinal);
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            _blocked = (await db.Players.Where(p => p.Blocked).Select(p => p.Xuid).ToListAsync(ct).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "No se pudo cargar la lista de permitidos o de bloqueados");
        }
    }

    private async Task SetBlockedAsync(CubeKeeperDbContext db, string xuid, string? name, bool blocked, CancellationToken ct)
    {
        var player = await db.Players.FindAsync([xuid], ct).ConfigureAwait(false);
        if (player is null)
        {
            if (!blocked)
            {
                return;
            }
            var now = _time.GetUtcNow();
            player = new Player { Xuid = xuid, Name = name ?? xuid, FirstSeen = now, LastSeen = now };
            db.Players.Add(player);
        }
        player.Blocked = blocked;
        player.BlockedAt = blocked ? _time.GetUtcNow() : null;
    }

    /// <summary>
    /// El seguimiento de jugadores también crea fichas de jugador: si los dos escriben la misma a la vez, uno choca con
    /// la clave única. Se reintenta una vez con un contexto nuevo, que ya verá la ficha creada por el otro.
    /// </summary>
    private async Task WithRetryAsync(Func<CubeKeeperDbContext, Task> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await work(db).ConfigureAwait(false);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                // Reintento.
            }
        }
    }

    private async Task WriteAllowListPropertyAsync(bool value, CancellationToken ct)
    {
        if (!File.Exists(_paths.ServerProperties))
        {
            return;
        }
        var doc = await ServerPropertiesDocument.LoadAsync(_paths.ServerProperties, ct).ConfigureAwait(false);
        if (doc.Set("allow-list", value ? "true" : "false"))
        {
            await doc.SaveAsync(_paths.ServerProperties, ct).ConfigureAwait(false);
        }
    }

    private async Task TryCommandAsync(string command, CancellationToken ct)
    {
        if (_server.Snapshot.Status != ServerStatus.Running)
        {
            return;
        }
        try
        {
            await _server.SendCommandAsync(command, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Se acaba de parar.
        }
    }

    private async Task<AllowListDocument> LoadAllowListAsync(CancellationToken ct) =>
        File.Exists(AllowListPath) ? AllowListDocument.Parse(await File.ReadAllTextAsync(AllowListPath, ct).ConfigureAwait(false)) : AllowListDocument.Empty();

    private async Task<PermissionsDocument> LoadPermissionsAsync(CancellationToken ct) =>
        File.Exists(PermissionsPath) ? PermissionsDocument.Parse(await File.ReadAllTextAsync(PermissionsPath, ct).ConfigureAwait(false)) : PermissionsDocument.Empty();

    private static async Task SaveAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    private static void ValidateXuid(string xuid)
    {
        if (string.IsNullOrWhiteSpace(xuid) || !xuid.All(char.IsAsciiDigit) || xuid.Length > 32)
        {
            throw new ArgumentException("XUID no válido.", nameof(xuid));
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Suscriptor de acceso con error");
        }
    }
}
