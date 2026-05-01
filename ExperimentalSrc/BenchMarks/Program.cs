using System.Linq;
using BenchmarkDotNet.Running;
using InductorParser.Benchmarks;

namespace InductorParser.StateMachine.Benchmarks;

// Entry point for the state-machine-only benchmark project.
// Mirrors the pattern of src/Benchmarks/Program.cs but only handles
// SM-specific dispatch:
//   --state-machine-compare: hand-timed Stopwatch comparison between
//     the recursive evaluator and the SM evaluator (StateMachineBench).
//   default: BenchmarkDotNet on the assembly's [Benchmark]-tagged
//     methods (StateMachineJsonBench).
//
// The Rebar runner (Rebar/RebarRunner.csproj) is its own console app
// invoked separately, same as it was before the split.
public class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--state-machine-compare"))
        {
            return StateMachineBench.Run();
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
