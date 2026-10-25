namespace GitWizardTests.TestSupport;

[TestFixture]
public class AsyncConditionTests
{
    [Test]
    public void PredicateExceptionIncludesOriginalFailureAndCondition()
    {
#pragma warning disable CA2201 // Simulate runtime NullReferenceException for diagnostic assertion
        var original = new NullReferenceException("source slot was null");
#pragma warning restore CA2201
        bool BrokenCondition() => throw original;

        var failure = Assert.ThrowsAsync<AssertionException>(async () =>
            await AsyncCondition.WaitUntilAsync(() => BrokenCondition()));

        Assert.That(failure!.InnerException, Is.SameAs(original));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Message, Does.Contain("BrokenCondition()"));
            Assert.That(failure.Message, Does.Contain("1 poll(s)"));
            Assert.That(failure.Message, Does.Contain("elapsed"));
            Assert.That(failure.Message, Does.Contain("NullReferenceException"));
            Assert.That(failure.Message, Does.Contain("source slot was null"));
            Assert.That(failure.InnerException!.StackTrace, Does.Contain("BrokenCondition"));
        }
    }

    [Test]
    public Task SatisfiedConditionCompletes() =>
        AsyncCondition.WaitUntilAsync(() => true, TimeSpan.Zero);

    [Test]
    public void UnsatisfiedConditionReportsTimeout()
    {
        var failure = Assert.ThrowsAsync<AssertionException>(async () =>
            await AsyncCondition.WaitUntilAsync(() => false, TimeSpan.Zero));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("Timed out"));
            Assert.That(failure.Message, Does.Contain("() => false"));
            Assert.That(failure.Message, Does.Contain("1 poll(s)"));
            Assert.That(failure.Message, Does.Contain("elapsed"));
        }
    }
}
