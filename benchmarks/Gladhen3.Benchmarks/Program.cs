using BenchmarkDotNet.Running;

namespace Gladhen3.Benchmarks;

/*
 * Entry point.  Always in Release; a Debug run measures the debugger.
 *
 * Everything, properly:
 *	dotnet run -c Release --project benchmarks/Gladhen3.Benchmarks --
 *	    --filter "*"
 *
 * One class:
 *	dotnet run -c Release --project benchmarks/Gladhen3.Benchmarks --
 *	    --filter "*StreamBenchmarks*"
 *
 * A smoke pass that runs each benchmark once, which is what CI does so the
 * benchmarks keep compiling and working:
 *	dotnet run -c Release --project benchmarks/Gladhen3.Benchmarks --
 *	    --filter "*" --job dry
 *
 * Results land in BenchmarkDotNet.Artifacts/ under the working directory.
 */
internal static class Program
{
	private static void
	Main(string[] args)
	{

		BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
		    .Run(args);
	}
}
