using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;

using Moq;

using Xunit;

using Dff = DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.GuiTests.Infrastructure.Services;

[SuppressMessage("Usage",
    "xUnit1051:Calls to methods which accept CancellationToken should use TestContext.Current.CancellationToken")]
public sealed class ScanCoordinatorTests
{
    [AvaloniaFact]
    public async Task RunScanWithDialogCoreAsync_Success_RaisesIndexedAndCompleted()
    {
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo.Object);
        host.Setup(x => x.WhenIndexesRebuiltAsync(5, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var dialogService = new TestDialogService();
        var finder = new Dff.DuplicateFileFinder(host.Object);
        var sut = new ScanCoordinator(host.Object, finder, dialogService);

        ScanIndexedEventArgs? indexed = null;
        ScanCompletedEventArgs? completed = null;

        sut.ScanIndexed += (_, e) => indexed = e;
        sut.ScanCompleted += (_, e) => completed = e;

        var completion = new ScanCompletionInfo(
            ScanRootId: 123,
            Generation: 5,
            ScanSequence: 77);

        await InvokeCoreAsync(
            sut,
            arg: "root-path",
            runAsync: (_, _) => Task.FromResult(completion));

        Assert.False(sut.IsScanning);

        Assert.NotNull(indexed);
        Assert.Equal("root-path", indexed.Arg);
        Assert.Equal(123, indexed.ScanRootId);
        Assert.Equal(5, indexed.Generation);

        Assert.NotNull(completed);
        Assert.Equal("root-path", completed.Arg);
        Assert.False(completed.Cancelled);
        Assert.Null(completed.Error);

        host.VerifyGet(x => x.Repo, Times.AtLeastOnce);
        host.Verify(x => x.WhenIndexesRebuiltAsync(5, It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
    }

    [AvaloniaFact]
    public async Task RunScanWithDialogCoreAsync_WaitsForIndexesRebuiltBeforeCompleting()
    {
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo.Object);

        var rebuildTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Setup(x => x.WhenIndexesRebuiltAsync(9, It.IsAny<CancellationToken>()))
            .Returns(rebuildTcs.Task);

        using var dialogService = new TestDialogService();
        var finder = new Dff.DuplicateFileFinder(host.Object);
        var sut = new ScanCoordinator(host.Object, finder, dialogService);

        ScanIndexedEventArgs? indexed = null;
        sut.ScanIndexed += (_, e) => indexed = e;

        var completion = new ScanCompletionInfo(
            ScanRootId: 42,
            Generation: 9,
            ScanSequence: 1);

        var runTask = InvokeCoreAsync(
            sut,
            arg: "rescan",
            runAsync: (_, _) => Task.FromResult(completion));

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(runTask.IsCompleted);
        Assert.Null(indexed);

        rebuildTcs.SetResult();

        await runTask;

        Assert.NotNull(indexed);
        Assert.Equal(42, indexed!.ScanRootId);
        Assert.Equal(9, indexed.Generation);

        host.VerifyGet(x => x.Repo, Times.AtLeastOnce);
        host.Verify(x => x.WhenIndexesRebuiltAsync(9, It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
    }

    [AvaloniaFact]
    public async Task RunScanWithDialogCoreAsync_RecoveredMissingPath_RaisesIndexedAndCompleted()
    {
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo.Object);

        using var dialogService = new TestDialogService();
        var finder = new Dff.DuplicateFileFinder(host.Object);
        var sut = new ScanCoordinator(host.Object, finder, dialogService);

        ScanIndexedEventArgs? indexed = null;
        ScanCompletedEventArgs? completed = null;

        sut.ScanIndexed += (_, e) => indexed = e;
        sut.ScanCompleted += (_, e) => completed = e;

        await InvokeCoreAsync(
            sut,
            arg: 55,
            runAsync: (_, _) => throw new DirectoryNotFoundException("missing"),
            tryRecoverMissingPathAsync: (_, _) =>
                Task.FromResult(new MissingPathResult(true, 17, 55)),
            recoveryWorkingText: "Recovering...");

        Assert.False(sut.IsScanning);

        Assert.NotNull(indexed);
        Assert.Equal(55, indexed.Arg);
        Assert.Equal(55, indexed.ScanRootId);
        Assert.Equal(17, indexed.Generation);

        Assert.NotNull(completed);
        Assert.Equal(55, completed.Arg);
        Assert.False(completed.Cancelled);
        Assert.Null(completed.Error);

        host.VerifyGet(x => x.Repo, Times.AtLeastOnce);

        repo.VerifyNoOtherCalls();
    }

    [AvaloniaFact]
    public async Task RunScanWithDialogCoreAsync_Failure_RethrowsAndRaisesCompletedWithError()
    {
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo.Object);

        using var dialogService = new TestDialogService();
        var finder = new Dff.DuplicateFileFinder(host.Object);
        var sut = new ScanCoordinator(host.Object, finder, dialogService);

        ScanIndexedEventArgs? indexed = null;
        ScanCompletedEventArgs? completed = null;

        sut.ScanIndexed += (_, e) => indexed = e;
        sut.ScanCompleted += (_, e) => completed = e;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeCoreAsync(
                sut,
                arg: "bad-scan",
                runAsync: (_, _) => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", ex.Message);
        Assert.False(sut.IsScanning);

        Assert.Null(indexed);

        Assert.NotNull(completed);
        Assert.Equal("bad-scan", completed.Arg);
        Assert.False(completed.Cancelled);
        Assert.NotNull(completed.Error);
        Assert.Equal("boom", completed.Error!.Message);

        host.VerifyGet(x => x.Repo, Times.AtLeastOnce);

        repo.VerifyNoOtherCalls();
    }

    private static Task InvokeCoreAsync(
        ScanCoordinator sut,
        object arg,
        Func<IProgress<Dff.DuplicateFileFinderProgressReport>, CancellationToken, Task<ScanCompletionInfo>> runAsync,
        Func<DirectoryNotFoundException, CancellationToken, Task<MissingPathResult>>? tryRecoverMissingPathAsync = null,
        string? recoveryWorkingText = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new ScanRunSpec(
            Arg: arg,
            RunAsync: runAsync,
            StartLog: () => { },
            CancelLog: () => { },
            FailLog: _ => { },
            TryHandleMissingPathAsync: tryRecoverMissingPathAsync,
            MissingPathWorkingText: recoveryWorkingText);

        return sut.RunScanWithDialogCoreAsync(spec, cancellationToken);
    }

    private sealed class TestDialogService : IDialogService, IDisposable
    {
        private readonly Window _owner;

        public TestDialogService()
        {
            _owner = new Window();
            _owner.Show();
        }

        public Window GetOwnerWindow() => _owner;

        public void Dispose()
        {
            try
            {
                if (_owner.IsVisible)
                    _owner.Close();
            }
            catch
            {
                // ignore
            }
        }

        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmationAsync(
            string title,
            string message,
            string okText = "OK",
            string cancelText = "Cancel")
            => Task.FromResult(true);

        public Task<bool> ShowActionDialogAsync(
            string title,
            string message,
            Func<CancellationToken, Action<string>, Task<(bool ok, string? error)>> action,
            string okText = "OK",
            string cancelText = "Cancel",
            string workingText = "Working...")
            => Task.FromResult(true);

        public Task<string?> ShowFolderPickerDialogAsync(
            string title,
            string? initialDirectory = null)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowOpenFileDialogAsync(
            string title,
            string? initialDirectory = null,
            IReadOnlyList<(string Description, string[] Extensions)>? filters = null)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowSaveFileDialogAsync(
            string title,
            string? initialDirectory = null,
            string? suggestedFileName = null,
            IReadOnlyList<(string Description, string[] Extensions)>? filters = null)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowTextInputAsync(
            string title,
            string message,
            string? initialText = null,
            string okText = "OK",
            string cancelText = "Cancel")
            => Task.FromResult<string?>(null);
    }
}
