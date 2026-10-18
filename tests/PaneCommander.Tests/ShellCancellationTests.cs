using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ShellCancellationTests
{
    [TestMethod]
    public void UserCancelledHresultWithAbortedFlagIsAnAbortedResult()
    {
        var queried=false;
        var result=ShellFileOperationService.CompleteShellExecution(()=>throw new COMException("cancel",unchecked((int)0x80270000)),()=>{queried=true;return true;});
        Assert.IsTrue(queried);Assert.IsTrue(result.Aborted);
    }
    [TestMethod]
    public void SuccessfulHresultStillQueriesAbortedState()
    {
        Assert.IsTrue(ShellFileOperationService.CompleteShellExecution(()=>{},()=>true).Aborted);
        Assert.IsFalse(ShellFileOperationService.CompleteShellExecution(()=>{},()=>false).Aborted);
    }
    [TestMethod]
    public void OtherFailureIsNotHiddenByAbortedFlag()
    {
        var queried=false;
        var failure=new COMException("access denied",unchecked((int)0x80070005));
        var caught=Assert.ThrowsExactly<COMException>(()=>ShellFileOperationService.CompleteShellExecution(()=>throw failure,()=>{queried=true;return true;}));
        Assert.AreSame(failure,caught);Assert.IsTrue(queried);
    }
    [TestMethod]
    public void CancellationHresultWithoutAbortedFlagRemainsAnError()
    {
        Assert.ThrowsExactly<COMException>(()=>ShellFileOperationService.CompleteShellExecution(()=>throw new COMException("cancel",unchecked((int)0x80270000)),()=>false));
    }
}
