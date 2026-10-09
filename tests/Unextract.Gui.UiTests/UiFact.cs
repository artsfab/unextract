using System.ComponentModel;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Unextract.Gui.UiTests;

// [UiFact] / [UiTheory]: the same as [Fact] / [Theory], except that when the test body fails, the test class
// (UiTestBase) saves its failure diagnostics while the GUI is still in the failing state, before the class is
// disposed (normal close, and the fixture cleanup after it). xUnit 2 gives Dispose no way to know about a failure,
// so the hook sits in the test invoker: Before → body → (diagnostics on failure) → After → Dispose.
[XunitTestCaseDiscoverer("Unextract.Gui.UiTests.UiFactDiscoverer", "Unextract.Gui.UiTests")]
public sealed class UiFactAttribute : FactAttribute;

[XunitTestCaseDiscoverer("Unextract.Gui.UiTests.UiTheoryDiscoverer", "Unextract.Gui.UiTests")]
public sealed class UiTheoryAttribute : TheoryAttribute;

public sealed class UiFactDiscoverer(IMessageSink diagnosticMessageSink) : FactDiscoverer(diagnosticMessageSink)
{
    protected override IXunitTestCase CreateTestCase(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute) =>
        new UiTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
}

// Data rows become one UiTestCase each (pre-enumerated, as for [Theory] with serializable data).
public sealed class UiTheoryDiscoverer(IMessageSink diagnosticMessageSink) : TheoryDiscoverer(diagnosticMessageSink)
{
    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod,
        IAttributeInfo theoryAttribute, object[] dataRow) =>
        [new UiTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod, dataRow)];
}

public sealed class UiTestCase : XunitTestCase
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public UiTestCase() { }

    public UiTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions,
        ITestMethod testMethod, object[]? testMethodArguments = null)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, testMethodArguments) { }

    public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) =>
        new UiTestCaseRunner(this, DisplayName, SkipReason, constructorArguments, TestMethodArguments, messageBus, aggregator, cancellationTokenSource).RunAsync();
}

internal sealed class UiTestCaseRunner(IXunitTestCase testCase, string displayName, string skipReason, object[] constructorArguments,
    object[] testMethodArguments, IMessageBus messageBus, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    : XunitTestCaseRunner(testCase, displayName, skipReason, constructorArguments, testMethodArguments, messageBus, aggregator, cancellationTokenSource)
{
    protected override XunitTestRunner CreateTestRunner(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments,
        MethodInfo testMethod, object[] testMethodArguments, string skipReason, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) =>
        new UiTestRunner(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, skipReason, beforeAfterAttributes,
            aggregator, cancellationTokenSource);
}

internal sealed class UiTestRunner(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments, MethodInfo testMethod,
    object[] testMethodArguments, string skipReason, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator,
    CancellationTokenSource cancellationTokenSource)
    : XunitTestRunner(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, skipReason, beforeAfterAttributes,
        aggregator, cancellationTokenSource)
{
    protected override Task<decimal> InvokeTestMethodAsync(ExceptionAggregator aggregator) =>
        new UiTestInvoker(Test, MessageBus, TestClass, ConstructorArguments, TestMethod, TestMethodArguments, BeforeAfterAttributes, aggregator,
            CancellationTokenSource).RunAsync();
}

internal sealed class UiTestInvoker(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments, MethodInfo testMethod,
    object[] testMethodArguments, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator,
    CancellationTokenSource cancellationTokenSource)
    : XunitTestInvoker(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, beforeAfterAttributes, aggregator,
        cancellationTokenSource)
{
    protected override async Task<decimal> InvokeTestMethodAsync(object testClassInstance)
    {
        var test = testClassInstance as UiTestBase;
        if (test is not null)
        {
            test.TestName = Test.DisplayName;
            test.MethodName = TestMethod.Name;
        }
        decimal time = await base.InvokeTestMethodAsync(testClassInstance);
        // SaveFailureDiagnostics never throws: a failed capture is written to the test output and the diagnostics folder.
        if (Aggregator.HasExceptions && test is not null) test.SaveFailureDiagnostics(Aggregator.ToException());
        return time;
    }
}
