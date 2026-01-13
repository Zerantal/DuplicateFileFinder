using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

/// <summary>
/// Projects ScanRootsTreeNode models into FolderNodeViewModel instances and wires lazy expansion.
/// </summary>
public sealed class FolderNodeViewModelFactory
{
    private readonly IScanRootsTreeNodeActions _actions;
    private readonly ScanRootsTreeBuilder _builder;

    public FolderNodeViewModelFactory(
        IScanRootsTreeNodeActions actions,
        ScanRootsTreeBuilder builder)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public FolderNodeViewModel CreateVm(
        ScanRootsTreeNode model,
        FolderNodeViewModel? parent,
        RepoSnapshotView snapshot,
        Action<FolderNodeViewModel> register)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(snapshot);

        var vm = new FolderNodeViewModel(model, _actions) { Parent = parent };

        register(vm);

        // UI-only: show expand arrow before children are loaded.
        if (model is { HasLazyChildren: true, ChildrenMaterialized: false } && vm.Children.Count == 0)
            vm.AddDummyChild();

        // Lazy children: when expanded, ask builder to materialize model children, then project to VMs.
        vm.EnsureChildrenLoaded = nodeVm =>
        {
            // already materialized? (dummy child removed and children present)
            if (!nodeVm.HasDummyChild)
                return;

            // Materialize the model tree first (builder uses indexes/snapshot)
            _builder.EnsureChildrenLoaded(nodeVm.Model);

            nodeVm.ClearChildren();
            foreach (var childModel in nodeVm.Model.Children)
                nodeVm.Children.Add(CreateVm(childModel, nodeVm, snapshot, register));
        };

        // Eager materialize if builder already provided children in the model (optional path).
        // If children are already present, don't leave dummy.
        if (model.Children.Count > 0)
        {
            vm.ClearChildren();
            foreach (var childModel in model.Children)
                vm.Children.Add(CreateVm(childModel, vm, snapshot, register));
        }

        return vm;
    }
}
