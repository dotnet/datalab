using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Woodstar.Benchmark;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<Benchmarks>(DefaultConfig.Instance, args);
    }
}

