// DuplicateFileFinder.GuiTests/Features/Duplicates/ScanRootsTree/ScanRootsTreeNodeActionsTests.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Repository.Core.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.ScanRootsTree;

public sealed class ScanRootsTreeNodeActionsTests
{
    [Fact]
    public async Task RescanFolderAsync_DelegatesToScanCoordinator()
    {
        var env = CreateSut();

        var dir = new DirHandle(ScanRootId: 3, Index: 10);

        await env.Actions.RescanFolderAsync(dir);

        var called = Assert.Single(env.Scanner.RescannedFolders);
        Assert.Equal(dir, called);
    }

    [Fact]
    public async Task RescanScanRootAsync_DelegatesToScanCoordinator()
    {
        var env = CreateSut();

        await env.Actions.RescanScanRootAsync(scanRootId: 42);

        Assert.Single(env.Scanner.RescannedScanRoots);
        Assert.Equal(42L, env.Scanner.RescannedScanRoots[0]);
    }

    [Fact]
    public async Task TryRemoveScanRootAsync_WhenCancelled_DoesNotRemove()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = false;

        var ok = await env.Actions.TryRemoveScanRootAsync(scanRootId: 7);

        Assert.False(ok);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Empty(env.Scanner.RemovedScanRoots);
    }

    [Fact]
    public async Task TryRemoveScanRootAsync_WhenConfirmed_RemovesAndReturnsTrue()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;

        var ok = await env.Actions.TryRemoveScanRootAsync(scanRootId: 7);

        Assert.True(ok);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Scanner.RemovedScanRoots);
        Assert.Equal(7L, env.Scanner.RemovedScanRoots[0]);
    }

    [Fact]
    public async Task TrySetScanRootDisplayNameAsync_WhenCancelled_ReturnsFalse_AndDoesNotCallScanner()
    {
        var env = CreateSut();
        env.Dialogs.NextTextInput = null;

        var ok = await env.Actions.TrySetScanRootDisplayNameAsync(scanRootId: 9, currentLabel: "Old");

        Assert.False(ok);

        Assert.Single(env.Dialogs.TextInputs);
        Assert.Empty(env.Scanner.DisplayNameUpdates);
    }

    [Fact]
    public async Task TrySetScanRootDisplayNameAsync_WhenBlank_CallsScannerWithNull_AndReturnsTrue()
    {
        var env = CreateSut();
        env.Dialogs.NextTextInput = "   ";

        var ok = await env.Actions.TrySetScanRootDisplayNameAsync(scanRootId: 9, currentLabel: "Old");

        Assert.True(ok);

        var update = Assert.Single(env.Scanner.DisplayNameUpdates);
        Assert.Equal(9L, update.ScanRootId);
        Assert.Null(update.DisplayName);
    }

    [Fact]
    public async Task TrySetScanRootDisplayNameAsync_WhenProvided_CallsScannerWithValue_AndReturnsTrue()
    {
        var env = CreateSut();
        env.Dialogs.NextTextInput = "My Root";

        var ok = await env.Actions.TrySetScanRootDisplayNameAsync(scanRootId: 9, currentLabel: "Old");

        Assert.True(ok);

        var update = Assert.Single(env.Scanner.DisplayNameUpdates);
        Assert.Equal(9L, update.ScanRootId);
        Assert.Equal("My Root", update.DisplayName);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static Sut CreateSut()
    {
        var repo = new FakeRepo([]);
        var host = new FakeRepoHost(repo);

        var dialogs = new FakeDialogService();
        var deleter = new FakeFileSystemDeleteService();
        var scanner = new RecordingScanCoordinator();

        var actions = new ScanRootsTreeNodeActions(
            host: host,
            scanner: scanner,
            dialogs: dialogs,
            deleter: deleter);

        return new Sut(actions, scanner, dialogs);
    }

    private sealed record Sut(
        ScanRootsTreeNodeActions Actions,
        RecordingScanCoordinator Scanner,
        FakeDialogService Dialogs);

    private sealed class RecordingScanCoordinator : IScanCoordinator
    {
        public bool IsScanning => false;

        public event EventHandler<DuplicateFileFinderProgressReport>? ProgressChanged;
        public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

        public readonly List<ScanRootId> RescannedScanRoots = [];
        public readonly List<DirHandle> RescannedFolders = [];
        public readonly List<ScanRootId> RemovedScanRoots = [];
        public readonly List<(ScanRootId ScanRootId, string? DisplayName)> DisplayNameUpdates = [];

        public Task RunScanNewLocationWithDialogAsync(string rootPath, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task RunRescanLocationWithDialogAsync(ScanRootId scanRootId, CancellationToken cancellationToken)
        {
            RescannedScanRoots.Add(scanRootId);
            return Task.CompletedTask;
        }

        // ReSharper disable once UnusedMember.Local
        public Task RunFolderRescanWithDialogAsync(DirHandle startDir)
        {
            RescannedFolders.Add(startDir);
            return Task.CompletedTask;
        }

        public Task RunFolderRescanWithDialogAsync(DirHandle startDir, CancellationToken cancellationToken)
        {
            RescannedFolders.Add(startDir);
            return Task.CompletedTask;
        }

        public Task RemoveScanRoot(ScanRootId scanRootId)
        {
            RemovedScanRoots.Add(scanRootId);
            return Task.CompletedTask;
        }

        public void CancelScan() => throw new NotImplementedException();

        public Task SetScanRootDisplayName(ScanRootId scanRootId, string? displayName)
        {
            DisplayNameUpdates.Add((scanRootId, displayName));
            return Task.CompletedTask;
        }
    }
}

