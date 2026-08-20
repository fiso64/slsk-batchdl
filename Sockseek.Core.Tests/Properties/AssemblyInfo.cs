using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Workers = 8, Scope = ExecutionScope.ClassLevel)]
