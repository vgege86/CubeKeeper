namespace CubeKeeper.Core.Abstractions;

public enum ConsoleLineKind
{
    /// <summary>Salida estándar de bedrock_server.</summary>
    Output,
    /// <summary>Salida de error de bedrock_server.</summary>
    Error,
    /// <summary>Comando enviado desde el panel.</summary>
    Command,
    /// <summary>Mensaje del propio CubeKeeper (arranque, parada, caídas…).</summary>
    System,
}

public sealed record ConsoleLine(long Sequence, DateTimeOffset Timestamp, ConsoleLineKind Kind, string Text);

/// <summary>Búfer circular con las últimas líneas de consola y aviso de las nuevas.</summary>
public interface IConsoleLog
{
    IReadOnlyList<ConsoleLine> Snapshot();

    event Action<ConsoleLine>? LineAdded;

    ConsoleLine Append(ConsoleLineKind kind, string text);
}
