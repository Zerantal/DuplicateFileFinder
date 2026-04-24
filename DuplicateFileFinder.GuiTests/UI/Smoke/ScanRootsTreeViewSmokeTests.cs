using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.Views.ScanRoots;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

public sealed class ScanRootsTreeViewSmokeTests
{
    [AvaloniaFact]
    public void ScanRootsTreeView_HasExpectedParts()
    {
        var vm = new ScanRootsTreeDesignViewModel();
        var window = CreateWindow(vm, ThemeVariant.Light);
        var view = GetView(window);

        Assert.NotNull(view.FindControl<Border>("PART_HeaderHost"));
        Assert.NotNull(view.FindControl<ScrollViewer>("PART_Scroller"));
        Assert.NotNull(view.FindControl<Border>("PART_RowsHost"));
        Assert.NotNull(view.FindControl<ItemsRepeater>("PART_Repeater"));
        Assert.NotNull(view.FindControl<Border>("PART_EmptyState"));
    }

    [AvaloniaFact]
    public void ScanRootsTreeView_EmptyState_TogglesWithRows()
    {
        var vm = new ScanRootsTreeDesignViewModel();
        var window = CreateWindow(vm, ThemeVariant.Light);
        var view = GetView(window);

        var scroller = view.FindControl<ScrollViewer>("PART_Scroller");
        var empty = view.FindControl<Border>("PART_EmptyState");
        Assert.NotNull(scroller);
        Assert.NotNull(empty);

        Assert.True(scroller.IsVisible);
        Assert.False(empty.IsVisible);

        vm.Rows.Clear();
        LayoutTestHelpers.DoLayout(window, 980, 360);

        Assert.False(scroller.IsVisible);
        Assert.True(empty.IsVisible);
    }

    [AvaloniaFact]
    public void ScanRootsTreeView_HeaderAndRows_GutterSyncsWithScrollbar()
    {
        var vm = new ScanRootsTreeDesignViewModel();
        var window = CreateWindow(vm, ThemeVariant.Light);
        var view = GetView(window);

        var headerHost = view.FindControl<Border>("PART_HeaderHost");
        var rowsHost = view.FindControl<Border>("PART_RowsHost");
        Assert.NotNull(headerHost);
        Assert.NotNull(rowsHost);

        // With many design rows there should be a vertical scrollbar + non-zero right gutter.
        Assert.True(headerHost.Padding.Right > 4);
        Assert.True(rowsHost.Padding.Right > 0);

    }

    [AvaloniaFact]
    public void ScanRootsTreeView_RowAndNumericCellClasses_AreApplied()
    {
        var vm = new ScanRootsTreeDesignViewModel();
        var window = CreateWindow(vm, ThemeVariant.Light);

        var row = window.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(x => x.Classes.Contains("scanroots-row"));

        Assert.NotNull(row);

        var numberCell = window.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(x => x.Classes.Contains("scanroots-number-cell"));

        Assert.NotNull(numberCell);
    }

    [AvaloniaFact]
    public void ScanRootsTreeView_Snapshot_LightTheme()
    {
        var vm = new ScanRootsTreeDesignViewModel();
        var window = CreateWindow(vm, ThemeVariant.Light);

        var png = CapturePng(window);
        AssertSnapshot("ScanRootsTreeView.light", png);
    }

    [AvaloniaFact]
    public void ScanRootsTreeView_Snapshot_DarkTheme()
    {
        var vm = new ScanRootsTreeDesignViewModel();
        var window = CreateWindow(vm, ThemeVariant.Dark);

        var png = CapturePng(window);
        AssertSnapshot("ScanRootsTreeView.dark", png);
    }

    private static Window CreateWindow(ScanRootsTreeDesignViewModel vm, ThemeVariant theme)
    {
        var view = new ScanRootsTreeView { DataContext = vm };
        var window = new Window
        {
            Content = view,
            RequestedThemeVariant = theme
        };

        window.Show();
        LayoutTestHelpers.DoLayout(window, 980, 360);
        return window;
    }

    private static ScanRootsTreeView GetView(Window window)
        => Assert.IsType<ScanRootsTreeView>(window.Content);

    private static byte[] CapturePng(Window window)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        var bitmap = window.CaptureRenderedFrame() ?? window.GetLastRenderedFrame();
        Assert.NotNull(bitmap);

        using var ms = new MemoryStream();
        bitmap.Save(ms);
        var png = ms.ToArray();
        Assert.True(png.Length > 0, "Captured snapshot PNG was empty.");
        return png;
    }

    private static void AssertSnapshot(string snapshotName, byte[] pngBytes)
    {
        var snapshotsDir = GetSnapshotsDirectory();
        Directory.CreateDirectory(snapshotsDir);

        var hashPath = Path.Combine(snapshotsDir, $"{snapshotName}.sha256");
        var pngPath = Path.Combine(snapshotsDir, $"{snapshotName}.png");
        var actualPath = Path.Combine(snapshotsDir, $"{snapshotName}.actual.png");

        var hash = Convert.ToHexString(SHA256.HashData(pngBytes));
        var update = string.Equals(
            Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        if (update || !File.Exists(hashPath))
        {
            File.WriteAllText(hashPath, hash + Environment.NewLine);
            File.WriteAllBytes(pngPath, pngBytes);
            return;
        }

        var expected = File.ReadAllText(hashPath).Trim();
        if (string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
            return;

        File.WriteAllBytes(actualPath, pngBytes);
        Assert.Fail(
            $"Snapshot mismatch for '{snapshotName}'. Expected hash={expected}, actual hash={hash}. " +
            $"Wrote actual image to: {actualPath}. " +
            "Set UPDATE_SNAPSHOTS=1 to accept updated snapshots.");
    }

    private static string GetSnapshotsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "DuplicateFileFinder.GuiTests", "UI", "Snapshots");
            if (Directory.Exists(Path.Combine(dir.FullName, "DuplicateFileFinder.GuiTests")))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root for snapshot storage.");
    }
}
