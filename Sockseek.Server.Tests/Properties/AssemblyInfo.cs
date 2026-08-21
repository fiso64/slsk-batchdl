using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Workers = 12, Scope = ExecutionScope.ClassLevel)]
