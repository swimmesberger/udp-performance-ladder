using BenchmarkDotNet.Running;
using MicroBenchmarks;

BenchmarkRunner.Run<ReceivePathBenchmarks>(args: args);
