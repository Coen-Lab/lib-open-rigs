using Microsoft.VisualStudio.TestTools.UnitTesting;

// Each test logs to a folder of its own (LoggingTestBase) through a Bonsai process of its own, with
// no ports, so tests run side by side, one per processor.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]
