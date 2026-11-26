using System;
using TypeSharp.VM.Interpreter;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class ExecutionProfileKeyTests
{
    [Fact]
    public void ModuleAndFunctionNamesRemainIndependent()
    {
        var profile = new ExecutionProfile();
        const string moduleA = "game::Enemy";
        const string functionB = "Enemy::update";
        const string displayKey = "game::Enemy::update";

        long firstA = profile.RecordCall(moduleA, "update");
        long secondA = profile.RecordCall(moduleA, "update");
        long beforeB = profile.GetCallCount("game", functionB);
        long recordB = profile.RecordCall("game", functionB);
        long getA = profile.GetCallCount(moduleA, "update");
        long getB = profile.GetCallCount("game", functionB);
        long[] pairs = [beforeB, recordB, getA, getB];

        long[] ordinary = [
            profile.RecordCall("profile", "main"), profile.RecordCall("profile", "main"),
            profile.GetCallCount("profile", "main"), profile.GetCallCount("profile", "missing")
        ];
        long[] nullModule = [
            profile.RecordCall(null!, "solo"), profile.GetCallCount("", "solo"),
            profile.RecordCall("", "solo"), profile.GetCallCount(null!, "solo")
        ];
        long[] nullFunction = [
            profile.RecordCall("solo", null!), profile.GetCallCount("solo", ""),
            profile.RecordCall("solo", ""), profile.GetCallCount("solo", null!)
        ];
        var snapshot = profile.Snapshot();
        long[] ordinal = [
            profile.GetCallCount("Game", functionB), profile.GetCallCount(moduleA, "Update"),
            snapshot.ContainsKey("Game::Enemy::update") ? 1 : 0,
            snapshot.ContainsKey("game::Enemy::Update") ? 1 : 0
        ];
        long[] projection = [
            snapshot[displayKey], snapshot["profile::main"],
            snapshot["::solo"], snapshot["solo::"]
        ];
        long[] retention = [
            profile.RecordCall("profile", "main"), profile.GetCallCount("profile", "main"),
            profile.Snapshot()["profile::main"], snapshot["profile::main"], snapshot[displayKey]
        ];
        Console.WriteLine($"PAIR [{string.Join(",", pairs)}]");
        Assert.Equal(1L, firstA);
        Assert.Equal(2L, secondA);
        Console.WriteLine($"CONTROL initialA [{firstA},{secondA}] PASS");
        Check("ordinary-missing", [1, 2, 2, 0], ordinary);
        Check("null-module", [1, 1, 2, 2], nullModule);
        Check("null-function", [1, 1, 2, 2], nullFunction);
        Check("ordinal", [0, 0, 0, 0], ordinal);
        Check("legacy-snapshot", [3, 2, 2, 2], projection);
        Check("detached-snapshot", [3, 3, 3, 2, 3], retention);
        Console.WriteLine("ASSERT ExactModuleFunctionPairCounts expected [0,1,2,1]");
        Assert.Equal(new long[] { 0, 1, 2, 1 }, pairs);
    }

    private static void Check(string name, long[] expected, long[] observed)
    {
        Assert.Equal(expected, observed);
        Console.WriteLine($"CONTROL {name} [{string.Join(",", observed)}] PASS");
    }
}
