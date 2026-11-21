using BenchmarkDotNet.Running;

// Run all:            dotnet run -c Release --project benchmarks/MiniNet.Benchmarks -- --filter '*'
// Run one class:      dotnet run -c Release --project benchmarks/MiniNet.Benchmarks -- --filter '*Router*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
