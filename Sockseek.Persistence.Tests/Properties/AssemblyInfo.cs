using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Workers = 6, Scope = ExecutionScope.ClassLevel)]
