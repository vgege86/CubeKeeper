using System.Diagnostics;
using System.Text;

namespace CubeKeeper.Infrastructure.Process;

/// <summary>Envoltorio fino sobre el proceso hijo bedrock_server con stdin/stdout redirigidos.</summary>
internal sealed class ServerProcess : IDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ServerProcess(System.Diagnostics.Process process)
    {
        _process = process;
    }

    public int Id => _process.Id;

    public int ExitCode => _process.ExitCode;

    public bool HasExited => _process.HasExited;

    public static ServerProcess Start(
        string fileName,
        string? arguments,
        string workingDirectory,
        Action<string> onOutput,
        Action<string> onError)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            CreateNoWindow = true,
        };
        // bedrock_server carga sus .so desde su propio directorio.
        psi.Environment["LD_LIBRARY_PATH"] = workingDirectory;

        var p = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onError(e.Data);
            }
        };

        if (!p.Start())
        {
            p.Dispose();
            throw new InvalidOperationException($"No se pudo iniciar {fileName}.");
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new ServerProcess(p);
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Espera a que el proceso termine y a que se haya leído toda su salida.</summary>
    public Task WaitForExitAsync(CancellationToken ct = default) => _process.WaitForExitAsync(ct);

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
        }
    }

    public void Dispose()
    {
        _process.Dispose();
        _writeLock.Dispose();
    }
}
