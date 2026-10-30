using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Nachos.Abstractions.Stores;
using Nachos.Core.Deriver;
using Nachos.Core.Memory;
using Shouldly;

namespace Nachos.Architecture.Tests;

/// <summary>
/// Review Focus 5: no memory read outside the storage providers bypasses <see cref="VisibilityPolicy"/>. The rule reads
/// the IL of every method of every other <c>src</c> assembly, so a read hidden in an async state machine, iterator, lambda or local function is attributed to the
/// method that wrote it.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Type[] MemoryStores = [typeof(IConclusionStore), typeof(IPeerCardStore)];

    // Everything else on the memory stores is a read, so a read member added later is covered without editing this list.
    private static readonly HashSet<string> Writes = new(StringComparer.Ordinal)
    {
        nameof(IConclusionStore.CommitDerivationAsync),
        nameof(IConclusionStore.InsertAsync),
        nameof(IConclusionStore.TombstoneAsync),
        nameof(IConclusionStore.PurgeTombstonesAsync),
        nameof(IPeerCardStore.SetAsync),
    };

    // The Deriver's dedup lookups decide which of its own facts are new; they never return memory to a caller.
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        $"{typeof(ConclusionDeduplicator).FullName}.{nameof(IConclusionStore.FindExactAsync)}",
        $"{typeof(ConclusionDeduplicator).FullName}.{nameof(IConclusionStore.FindNearestAsync)}",
    };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    [Fact]
    public void MemoryReads_RequireVisibilityPolicy()
    {
        var reads = ScannedAssemblies().SelectMany(MemoryReadCalls).ToArray();

        // The scan must see through async state machines, or the rule would pass without checking anything.
        reads.ShouldContain(r => r.Owner.DeclaringType == typeof(ConclusionDeduplicator) && r.Read.Name == nameof(IConclusionStore.FindExactAsync));
        reads.ShouldContain(r => r.Owner.DeclaringType == typeof(RepresentationBuilder) && r.Read.Name == nameof(IConclusionStore.QueryAsync));
        reads.ShouldContain(r => r.Owner.DeclaringType == typeof(PeerContextService) && r.Read.DeclaringType == typeof(IPeerCardStore));

        reads.Where(r => !TakesPolicy(r.Owner) && !Exempt.Contains($"{r.Owner.DeclaringType!.FullName}.{r.Read.Name}"))
            .Select(r => $"{r.Owner.DeclaringType!.FullName}.{r.Owner.Name} calls {r.Read.DeclaringType!.Name}.{r.Read.Name}")
            .ShouldBeEmpty("memory reads outside a method that takes a VisibilityPolicy");
    }

    /// <summary>
    /// Every assembly built from <c>src</c>, except the storage providers (which implement the reads) and the Aspire
    /// AppHost (which only orchestrates). A new project fails here until this test project references it.
    /// </summary>
    private static Assembly[] ScannedAssemblies()
    {
        var names = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => !name.StartsWith("Nachos.DataLayer.", StringComparison.Ordinal) && name != "Nachos.AppHost")
            .Order(StringComparer.Ordinal)
            .ToArray();

        names.ShouldContain("Nachos.Api");
        foreach (var name in names)
        {
            File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll"))
                .ShouldBeTrue($"{name} is not scanned for memory reads; add a ProjectReference to it in Nachos.Architecture.Tests");
        }

        return [.. names.Select(name => Assembly.Load(new AssemblyName(name)))];
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Nachos.slnx was not found above the test output; the memory-read rule needs the source tree.");
    }

    private static bool TakesPolicy(MethodBase method) =>
        method.GetParameters().Any(p => p.ParameterType == typeof(VisibilityPolicy));

    private static IEnumerable<(MethodBase Owner, MethodBase Read)> MemoryReadCalls(Assembly assembly)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                foreach (var called in CalledMethods(method))
                {
                    if (MemoryStores.Contains(called.DeclaringType) && !Writes.Contains(called.Name))
                    {
                        foreach (var owner in SourceMethods(method))
                        {
                            yield return (owner, called);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Every method <paramref name="method"/>'s IL calls, loads or constructs.</summary>
    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            var value = il[position] == 0xFE ? (short)(0xFE00 | il[position + 1]) : il[position];
            var code = OpCodesByValue[value];
            position += code.Size;
            if (code.OperandType == OperandType.InlineMethod)
            {
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments)!;
            }

            position += OperandSize(code.OperandType, il, position);
        }
    }

    private static int OperandSize(OperandType operand, byte[] il, int position) => operand switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
        _ => 4,
    };

    /// <summary>
    /// The methods whose source contains <paramref name="method"/>'s body: itself, or for compiler-generated code (state
    /// machines, closures, lambdas, local functions) the user methods it was generated from.
    /// </summary>
    private static IEnumerable<MethodBase> SourceMethods(MethodBase method)
    {
        var type = method.DeclaringType!;
        if (IsGenerated(type) && (typeof(IAsyncStateMachine).IsAssignableFrom(type) || IsIterator(type)))
        {
            var outer = type.DeclaringType!;
            var builders = outer.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType == type)
                .ToArray();
            builders.ShouldNotBeEmpty($"no method of {outer.FullName} owns the state machine {type.Name}");
            return builders.SelectMany(SourceMethods);
        }

        if (!method.Name.StartsWith('<') && !IsGenerated(type))
        {
            return [method];
        }

        // Lambdas and local functions are named <UserMethod>b__… or <UserMethod>g__Local|…, in the user type or a nested closure type.
        var name = method.Name.StartsWith('<') ? method.Name[1..method.Name.IndexOf('>')] : null;
        var user = type;
        while (IsGenerated(user))
        {
            user = user.DeclaringType!;
        }

        var candidates = user.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(user.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.Name == name)
            .ToArray();
        candidates.ShouldNotBeEmpty($"generated method {type.FullName}.{method.Name} has no user method in {user.FullName}");
        return candidates;
    }

    private static bool IsGenerated(Type type) =>
        type.Name.StartsWith('<') || type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null && type.IsNested;

    private static bool IsIterator(Type type) =>
        type.GetInterfaces().Any(i => i == typeof(System.Collections.IEnumerator) || i.Name.StartsWith("IAsyncEnumerator", StringComparison.Ordinal));
}
