using CubeKeeper.Core.Server;

namespace CubeKeeper.Core.Tests;

public class CrashLoopGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Allows_up_to_max_restarts_inside_window()
    {
        var guard = new CrashLoopGuard(3, TimeSpan.FromMinutes(10));
        Assert.True(guard.TryRegisterRestart(T0));
        Assert.True(guard.TryRegisterRestart(T0.AddMinutes(1)));
        Assert.True(guard.TryRegisterRestart(T0.AddMinutes(2)));
        Assert.False(guard.TryRegisterRestart(T0.AddMinutes(3)));
    }

    [Fact]
    public void Old_crashes_expire()
    {
        var guard = new CrashLoopGuard(2, TimeSpan.FromMinutes(10));
        Assert.True(guard.TryRegisterRestart(T0));
        Assert.True(guard.TryRegisterRestart(T0.AddMinutes(1)));
        Assert.False(guard.TryRegisterRestart(T0.AddMinutes(5)));
        Assert.True(guard.TryRegisterRestart(T0.AddMinutes(12)));
    }

    [Fact]
    public void Reset_clears_history()
    {
        var guard = new CrashLoopGuard(1, TimeSpan.FromMinutes(10));
        Assert.True(guard.TryRegisterRestart(T0));
        Assert.False(guard.TryRegisterRestart(T0));
        guard.Reset();
        Assert.True(guard.TryRegisterRestart(T0));
    }

    [Fact]
    public void Zero_restarts_never_allows()
    {
        Assert.False(new CrashLoopGuard(0, TimeSpan.FromMinutes(1)).TryRegisterRestart(T0));
    }
}
