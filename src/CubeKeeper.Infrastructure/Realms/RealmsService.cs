using CubeKeeper.Core.Realms;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using CubeKeeper.Infrastructure.Worlds;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Realms;

public enum RealmsStatus
{
    Disconnected,
    WaitingForCode,
    Connected,
}

/// <summary>Lo que el panel necesita para pintarse.</summary>
public sealed record RealmsView(RealmsStatus Status, DeviceCode? Code, string? Gamertag, DateTimeOffset? ConnectedAt, string? LastError);

/// <summary>
/// Conecta una cuenta de Microsoft (código por dispositivo; CubeKeeper nunca ve la contraseña), lista sus Realms y descarga
/// el mundo de una ranura como .mcworld para importarlo con <see cref="WorldService"/>. Guarda solo el token de renovación y
/// la clave de firma, cifrados; los tokens de acceso y XSTS viven en memoria.
/// </summary>
public sealed class RealmsService : IDisposable
{
    public const string HttpClientName = "realms";

    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly RealmsAuth _auth;
    private readonly RealmsClient _client;
    private readonly AppStateStore _state;
    private readonly WorldService _worlds;
    private readonly TimeProvider _time;
    private readonly ILogger<RealmsService> _logger;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly Lock _lock = new();

    private CancellationTokenSource? _loginCts;
    private DeviceCode? _pendingCode;
    private string? _lastError;
    private MsaToken? _msa;
    private XstsToken? _xsts;
    private XboxRequestSigner? _signer;

    public RealmsService(
        HttpClient http,
        IOptions<CubeKeeperOptions> options,
        AppStateStore state,
        WorldService worlds,
        TimeProvider time,
        IDataProtectionProvider dataProtection,
        ILogger<RealmsService> logger)
    {
        var realms = options.Value.Realms;
        _auth = new RealmsAuth(http, realms, time);
        _client = new RealmsClient(http, realms, () =>
            !string.IsNullOrWhiteSpace(realms.ClientVersion) ? realms.ClientVersion : ShortVersion(state.Current.InstalledVersion));
        _state = state;
        _worlds = worlds;
        _time = time;
        _logger = logger;
        _protector = dataProtection.CreateProtector("CubeKeeper.Realms.Session");
    }

    public event Action? Changed;

    public RealmsView View
    {
        get
        {
            lock (_lock)
            {
                var session = _state.Current.Realms;
                var status = _pendingCode is not null ? RealmsStatus.WaitingForCode
                    : session is not null ? RealmsStatus.Connected
                    : RealmsStatus.Disconnected;
                return new RealmsView(status, _pendingCode, session?.Gamertag, session?.ConnectedAt, _lastError);
            }
        }
    }

    /// <summary>Pide un código y espera en segundo plano a que se use en microsoft.com/link.</summary>
    public async Task<DeviceCode> StartLoginAsync(CancellationToken ct = default)
    {
        CancelLogin();
        var code = await _auth.StartDeviceCodeAsync(ct).ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _loginCts = cts;
            _pendingCode = code;
            _lastError = null;
        }
        _ = Task.Run(() => WaitForLoginAsync(code, cts), CancellationToken.None);
        Changed?.Invoke();
        return code;
    }

    public void CancelLogin()
    {
        lock (_lock)
        {
            _loginCts?.Cancel();
            _loginCts = null;
            _pendingCode = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Olvida la cuenta: borra el token de renovación y la clave guardados.</summary>
    public void Disconnect()
    {
        CancelLogin();
        lock (_lock)
        {
            _msa = null;
            _xsts = null;
            _signer?.Dispose();
            _signer = null;
            _lastError = null;
        }
        _state.Update(s => s with { Realms = null });
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<RealmInfo>> ListAsync(CancellationToken ct = default)
    {
        var xsts = await GetXstsAsync(ct).ConfigureAwait(false);
        return await _client.GetRealmsAsync(xsts, ct).ConfigureAwait(false);
    }

    /// <summary>Descarga el mundo de una ranura y lo importa. Devuelve el nombre de la carpeta creada.</summary>
    public async Task<string> ImportAsync(RealmInfo realm, int slot, string? desiredName, bool activate, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var xsts = await GetXstsAsync(ct).ConfigureAwait(false);
        var download = await _client.GetDownloadAsync(xsts, realm.Id, slot, ct).ConfigureAwait(false);
        using var response = await _client.OpenDownloadAsync(download, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var counted = new ProgressStream(body, progress);
        var name = await _worlds.ImportAsync(counted, realm.Name + ".mcworld", desiredName, activate, ct).ConfigureAwait(false);
        _logger.LogInformation("Mundo de Realms importado: {Realm} ranura {Slot} → {World} ({Bytes} bytes)", realm.Name, slot, name, counted.BytesRead);
        return name;
    }

    private async Task WaitForLoginAsync(DeviceCode code, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            while (_time.GetUtcNow() < code.ExpiresAt)
            {
                await Task.Delay(code.Interval, _time, ct).ConfigureAwait(false);
                var msa = await _auth.PollDeviceCodeAsync(code, ct).ConfigureAwait(false);
                if (msa is null)
                {
                    continue;
                }
                var signer = XboxRequestSigner.CreateNew();
                var xsts = await _auth.GetXstsAsync(msa.AccessToken, signer, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    _signer?.Dispose();
                    _signer = signer;
                    _msa = msa;
                    _xsts = xsts;
                }
                SaveSession(msa.RefreshToken, signer, xsts);
                _logger.LogInformation("Cuenta de Realms conectada: {Gamertag}", xsts.Gamertag ?? xsts.Xuid);
                Finish(code, error: null);
                return;
            }
            Finish(code, "El código ha caducado sin usarse. Vuelve a empezar.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al conectar la cuenta de Realms");
            Finish(code, ex is RealmsAuthException ? ex.Message : "No se pudo conectar la cuenta: " + ex.Message);
        }
    }

    private void Finish(DeviceCode code, string? error)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_pendingCode, code))
            {
                return;
            }
            _pendingCode = null;
            _loginCts = null;
            _lastError = error;
        }
        Changed?.Invoke();
    }

    private async Task<XstsToken> GetXstsAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_xsts is { } cached && cached.ExpiresAt - ExpiryMargin > now)
            {
                return cached;
            }
            var session = _state.Current.Realms ?? throw new RealmsAuthException("No hay ninguna cuenta de Microsoft conectada.");
            XboxRequestSigner signer;
            string refreshToken;
            try
            {
                signer = _signer ??= XboxRequestSigner.Import(Convert.FromBase64String(_protector.Unprotect(session.ProtectedSigningKey)));
                refreshToken = _protector.Unprotect(session.ProtectedRefreshToken);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new RealmsAuthException("No se pudo descifrar la sesión guardada (¿se han perdido las claves de /data/keys?). Vuelve a conectar la cuenta.", ex);
            }

            if (_msa is not { } msa || msa.ExpiresAt - ExpiryMargin <= now)
            {
                msa = await _auth.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
                _msa = msa;
            }
            var xsts = await _auth.GetXstsAsync(msa.AccessToken, signer, ct).ConfigureAwait(false);
            _xsts = xsts;
            // Microsoft rota el token de renovación: se guarda el nuevo.
            SaveSession(msa.RefreshToken, signer, xsts);
            return xsts;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private void SaveSession(string refreshToken, XboxRequestSigner signer, XstsToken xsts)
    {
        var previous = _state.Current.Realms;
        _state.Update(s => s with
        {
            Realms = new RealmsSession(
                _protector.Protect(refreshToken),
                _protector.Protect(Convert.ToBase64String(signer.ExportPrivateKey())),
                xsts.Gamertag ?? previous?.Gamertag,
                xsts.Xuid ?? previous?.Xuid,
                previous?.ConnectedAt ?? _time.GetUtcNow()),
        });
    }

    /// <summary>Realms espera una versión de tres números (1.26.65), no la de cuatro del servidor.</summary>
    internal static string ShortVersion(string? installed)
    {
        if (string.IsNullOrWhiteSpace(installed))
        {
            return "0.0.0";
        }
        var parts = installed.Split('.');
        return parts.Length > 3 ? string.Join('.', parts.Take(3)) : installed;
    }

    public void Dispose()
    {
        _loginCts?.Cancel();
        _signer?.Dispose();
        _tokenGate.Dispose();
    }

    /// <summary>Cuenta los bytes leídos para mostrar el avance de la descarga.</summary>
    private sealed class ProgressStream(Stream inner, IProgress<long>? progress) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            BytesRead += read;
            progress?.Report(BytesRead);
            return read;
        }
    }
}
