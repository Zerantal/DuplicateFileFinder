// using System;
//
// using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
// using DuplicateFileFinder.GuiTests.UI.Fakes;
//
// using DuplicateFileFinderLib.Repository.Core.Models;
// using DuplicateFileFinderLib.Repository.Storage.Models;
//
// namespace DuplicateFileFinder.GuiTests;
//
// internal static class TestObjectFactory
// {
//     internal static FolderNodeViewModel CreateFolderNode(
//         string name,
//         string fullPath,
//         FolderNodeViewModel? parent,
//         FakeScanCoordinator? scanCoordinator = null,
//         FakeDialogService? dialogs = null,
//         FakeFileSystemDeleteService? deleter = null,
//         FakeRepo? repo = null,
//         DirHandle? dir = null,
//         long scanRootId = 1,
//         Action<FolderNodeViewModel>? ensureChildrenLoaded = null)
//     {
//         var node = new FolderNodeViewModel(
//             dir ?? new DirHandle(scanRootId, 1),
//             name,
//             fullPath,
//             scanCoordinator,
//             dialogs,
//             deleter,
//             repo,
//             scanRootId)
//         { EnsureChildrenLoaded = ensureChildrenLoaded, Parent = parent };
//
//         return node;
//     }
//
//     internal static ScanRoot NewScanRoot(long id, string path)
//         => new()
//         {
//             RootId = id,
//             RootPath = path,
//             VolumePath = null,
//             IsDeleted = false,
//             DirId = 1,
//             CreatedAt = DateTimeOffset.UtcNow
//         };
// }
