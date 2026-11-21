using CubeKeeper.Core.Resources;

namespace CubeKeeper.Core.Tests;

public class ProcFsTests
{
    [Fact]
    public void Reads_resident_memory_from_status() =>
        Assert.Equal(1_234_567L * 1024, ProcFs.ParseRssBytes("Name:\tbedrock_server\nVmPeak:\t  2000000 kB\nVmRSS:\t  1234567 kB\nThreads:\t40\n"));

    [Fact]
    public void Reads_cpu_ticks_even_with_spaces_in_the_name()
    {
        var stat = "4242 (bedrock server) S 1 4242 4242 0 -1 4194560 12345 0 0 0 1500 250 0 0 20 0 40 0 100 2000000 300000";
        Assert.Equal(1750, ProcFs.ParseCpuTicks(stat));
        Assert.Null(ProcFs.ParseCpuTicks("roto"));
    }

    [Theory]
    [InlineData("4294967296\n", 4294967296L)]
    [InlineData("max\n", null)]
    [InlineData("9223372036854771712", null)]
    public void Reads_cgroup_limits(string text, long? expected) => Assert.Equal(expected, ProcFs.ParseLimit(text));

    [Fact]
    public void Reads_memory_stat_fields()
    {
        const string stat = "anon 1000\nfile 5000\ninactive_file 3000\nactive_file 2000\n";
        Assert.Equal(3000, ProcFs.ParseStat(stat, "inactive_file"));
        Assert.Null(ProcFs.ParseStat(stat, "total_inactive_file"));
    }

    [Theory]
    [InlineData("400000 100000", 4.0)]
    [InlineData("150000 100000", 1.5)]
    [InlineData("max 100000", null)]
    public void Reads_cpu_quota(string text, double? expected) => Assert.Equal(expected, ProcFs.ParseCpuMax(text));
}
