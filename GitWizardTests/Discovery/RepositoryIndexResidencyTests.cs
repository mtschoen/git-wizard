using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Discovery;

/// <summary>The size pass must return mapped pages in a per-member finally block without closing the discovery index.</summary>
[TestFixture]
public class RepositoryIndexResidencyTests
{
    static readonly Dictionary<ushort, OpCode> OperationCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    sealed record DecodedInstruction(int Offset, OpCode OperationCode, MethodBase? Method, int? BranchTarget);

    [Test]
    public async Task Issue468_SizePassReleasesResidentPages()
    {
        AssertComputePerMemberFinallyRegion();

        using var directory = new TempDirectory("gw-residency-");
        var disposed = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(directory.Path),
            CacheMode = CacheMode.Disabled,
            Producer = ProducerSelection.WalkOnly
        }, CancellationToken.None);
        await disposed.DisposeAsync();

        // ThrowIfCancellationRequested runs before Compute reads the member, so only the per-member
        // finally calling ReleaseResidentPages on the disposed index surfaces ObjectDisposedException.
        Assert.That(
            () => RepositorySizeCalculator.Compute(
                new FileIndexSet([disposed]), [], new CancellationToken(canceled: true)),
            Throws.TypeOf<ObjectDisposedException>());
    }

    static void AssertComputePerMemberFinallyRegion()
    {
        var compute = typeof(RepositorySizeCalculator).GetMethod(
            nameof(RepositorySizeCalculator.Compute),
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var body = compute.GetMethodBody()!;
        var instructions = DecodeInstructions(compute);

        var currentMemberOffset = FindSingleCallOffset(instructions, method =>
            method is { Name: "get_Current", DeclaringType.IsGenericType: true } &&
            method.DeclaringType.GetGenericTypeDefinition() == typeof(IEnumerator<>) &&
            method.DeclaringType.GetGenericArguments()[0] == typeof(FileIndex));
        var cancellationOffset = FindSingleCallOffset(instructions,
            typeof(CancellationToken).GetMethod(nameof(CancellationToken.ThrowIfCancellationRequested))!);
        var snapshotCheckOffset = FindSingleCallOffset(instructions, method =>
            method.DeclaringType == typeof(RepositorySizeCalculator) && method.Name == "IsCompleteSnapshot");
        var findOffset = FindSingleCallOffset(instructions,
            typeof(FileIndex).GetMethod(nameof(FileIndex.Find))!);
        var locatedCountOffset = FindSingleCallOffset(instructions, method =>
            method is { Name: "get_Count", DeclaringType.IsGenericType: true } &&
            method.DeclaringType.GetGenericTypeDefinition() == typeof(List<>));
        var sumMemberOffset = FindSingleCallOffset(instructions, method =>
            method.DeclaringType == typeof(RepositorySizeCalculator) && method.Name == "SumMember");
        var exceptWithOffset = FindSingleCallOffset(instructions,
            typeof(HashSet<string>).GetMethod(nameof(HashSet<string>.ExceptWith))!);
        var releaseOffset = FindSingleCallOffset(instructions,
            typeof(FileIndex).GetMethod(nameof(FileIndex.ReleaseResidentPages))!);
        var moveNextOffset = FindSingleCallOffset(instructions,
            typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!);

        var perMemberClause = body.ExceptionHandlingClauses.SingleOrDefault(clause =>
            clause.Flags == ExceptionHandlingClauseOptions.Finally &&
            releaseOffset >= clause.HandlerOffset &&
            releaseOffset < clause.HandlerOffset + clause.HandlerLength);
        Assert.That(perMemberClause, Is.Not.Null,
            "Compute must call FileIndex.ReleaseResidentPages inside a finally handler.");

        var tryStart = perMemberClause!.TryOffset;
        var tryEnd = perMemberClause.TryOffset + perMemberClause.TryLength;
        var handlerEnd = perMemberClause.HandlerOffset + perMemberClause.HandlerLength;
        var enumeratorClause = body.ExceptionHandlingClauses.SingleOrDefault(clause =>
            clause != perMemberClause &&
            clause.Flags == ExceptionHandlingClauseOptions.Finally &&
            currentMemberOffset >= clause.TryOffset &&
            moveNextOffset < clause.TryOffset + clause.TryLength);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumeratorClause, Is.Not.Null,
                "The per-member finally must be nested inside the index.Indices enumerator loop.");
            Assert.That(currentMemberOffset, Is.LessThan(tryStart),
                "Each member must be fetched from the enumerator before entering the per-member try block.");
            Assert.That(handlerEnd, Is.LessThanOrEqualTo(moveNextOffset),
                "The per-member finally must finish before advancing the index.Indices enumerator.");
            Assert.That(cancellationOffset, Is.InRange(tryStart, snapshotCheckOffset - 1));
            Assert.That(snapshotCheckOffset, Is.InRange(cancellationOffset + 1, findOffset - 1));
            Assert.That(findOffset, Is.InRange(snapshotCheckOffset + 1, locatedCountOffset - 1));
            Assert.That(locatedCountOffset, Is.InRange(findOffset + 1, sumMemberOffset - 1));
            Assert.That(sumMemberOffset, Is.InRange(locatedCountOffset + 1, exceptWithOffset - 1));
            Assert.That(exceptWithOffset, Is.InRange(sumMemberOffset + 1, tryEnd - 1));
            Assert.That(HasLeaveToLoopStep(instructions, snapshotCheckOffset, findOffset, handlerEnd, moveNextOffset), Is.True,
                "Skipping an incomplete or walk-produced index must leave through the per-member finally.");
            Assert.That(HasLeaveToLoopStep(instructions, locatedCountOffset, sumMemberOffset, handlerEnd, moveNextOffset), Is.True,
                "Skipping an index with no located repositories must leave through the per-member finally.");
            Assert.That(HasLeaveToLoopStep(instructions, exceptWithOffset, tryEnd, handlerEnd, moveNextOffset), Is.True,
                "Completing a sized index must leave through the per-member finally after SumMember and ExceptWith.");
        }
    }

    static int FindSingleCallOffset(IEnumerable<DecodedInstruction> instructions, MethodInfo target) =>
        FindSingleCallOffset(instructions, method => method == target);

    static int FindSingleCallOffset(IEnumerable<DecodedInstruction> instructions, Func<MethodBase, bool> predicate)
    {
        var matches = instructions
            .Where(instruction => instruction.Method is { } method && predicate(method))
            .Select(instruction => instruction.Offset)
            .ToArray();
        Assert.That(matches, Has.Length.EqualTo(1));
        return matches[0];
    }

    static bool HasLeaveToLoopStep(
        IEnumerable<DecodedInstruction> instructions, int startOffset, int endOffset, int handlerEnd, int moveNextOffset) =>
        instructions.Any(instruction =>
            instruction.Offset > startOffset &&
            instruction.Offset < endOffset &&
            (instruction.OperationCode == OpCodes.Leave || instruction.OperationCode == OpCodes.Leave_S) &&
            instruction.BranchTarget is { } target &&
            target >= handlerEnd &&
            target <= moveNextOffset);

    static List<DecodedInstruction> DecodeInstructions(MethodInfo method)
    {
        var body = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException($"Missing IL body for {method.Name}.");
        var instructions = new List<DecodedInstruction>();
        for (var position = 0; position < body.Length;)
        {
            var instructionOffset = position;
            ushort value = body[position++];
            if (value == 0xfe)
            {
                value = (ushort)(0xfe00 | body[position++]);
            }

            var operationCode = OperationCodes[value];
            var resolvedMethod = operationCode.OperandType == OperandType.InlineMethod
                ? method.Module.ResolveMethod(BitConverter.ToInt32(body, position))
                : null;
            var operandSize = GetOperandSize(operationCode.OperandType, body, position);
            int? branchTarget = operationCode.OperandType switch
            {
                OperandType.InlineBrTarget => position + operandSize + BitConverter.ToInt32(body, position),
                OperandType.ShortInlineBrTarget => position + operandSize + unchecked((sbyte)body[position]),
                _ => null
            };
            position += operandSize;
            instructions.Add(new DecodedInstruction(instructionOffset, operationCode, resolvedMethod, branchTarget));
        }

        return instructions;
    }

    static int GetOperandSize(OperandType operandType, byte[] body, int position) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(body, position),
            _ => 4
        };
}
