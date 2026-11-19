using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Access;
using CubeKeeper.Core.GameRules;
using CubeKeeper.Infrastructure.Process;

namespace CubeKeeper.Infrastructure.Game;

/// <summary>Controles rápidos del juego (hora, clima, mensajes) y reglas del juego.</summary>
public sealed class GameControlService(IServerController server, ConsoleCommandRunner runner)
{
    public static readonly IReadOnlyDictionary<string, string> TimePresets = new Dictionary<string, string>
    {
        ["sunrise"] = "Amanecer",
        ["day"] = "Día",
        ["noon"] = "Mediodía",
        ["sunset"] = "Atardecer",
        ["night"] = "Noche",
        ["midnight"] = "Medianoche",
    };

    public static readonly IReadOnlyDictionary<string, string> WeatherPresets = new Dictionary<string, string>
    {
        ["clear"] = "Despejado",
        ["rain"] = "Lluvia",
        ["thunder"] = "Tormenta",
    };

    public Task SetTimeAsync(string preset, CancellationToken ct = default) =>
        TimePresets.ContainsKey(preset)
            ? server.SendCommandAsync($"time set {preset}", ct)
            : throw new ArgumentException("Hora no válida.", nameof(preset));

    public Task SetWeatherAsync(string preset, CancellationToken ct = default) =>
        WeatherPresets.ContainsKey(preset)
            ? server.SendCommandAsync($"weather {preset}", ct)
            : throw new ArgumentException("Clima no válido.", nameof(preset));

    public Task SayAsync(string message, CancellationToken ct = default)
    {
        var text = AccessPolicy.SafeText(message);
        if (text.Length == 0)
        {
            throw new ArgumentException("Escribe un mensaje.", nameof(message));
        }
        return server.SendCommandAsync($"say {text}", ct);
    }

    /// <summary>Lee las reglas con <c>gamerule</c>. Si BDS no responde con el formato esperado, devuelve lo que haya entendido.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetRulesAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string> last = new Dictionary<string, string>();
        try
        {
            return await runner.RunAsync("gamerule", lines =>
            {
                last = GameRuleCatalog.ParseValues(lines);
                return last.Count >= 10 ? last : null;
            }, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return last;
        }
    }

    public Task SetRuleAsync(string name, string value, CancellationToken ct = default) =>
        server.SendCommandAsync(GameRuleCatalog.SetCommand(name, value), ct);
}
