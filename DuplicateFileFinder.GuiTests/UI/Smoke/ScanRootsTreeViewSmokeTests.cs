using System;
using System.Diagnostics;
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
        var snapshotsRoot = GetSnapshotsRoot();
        var baselinesDir = Path.Combine(snapshotsRoot, "Baselines");
        var artifactsDir = Path.Combine(snapshotsRoot, "Artifacts");
        Directory.CreateDirectory(baselinesDir);
        Directory.CreateDirectory(artifactsDir);

        var hashPath = Path.Combine(baselinesDir, $"{snapshotName}.sha256");
        var pngPath = Path.Combine(baselinesDir, $"{snapshotName}.png");
        var actualPath = Path.Combine(artifactsDir, $"{snapshotName}.actual.png");
        var reportPath = Path.Combine(artifactsDir, $"{snapshotName}.diff.md");

        // Backward-compatible read path for older snapshots before Baselines/Artifacts split.
        var legacyHashPath = Path.Combine(snapshotsRoot, $"{snapshotName}.sha256");
        var legacyPngPath = Path.Combine(snapshotsRoot, $"{snapshotName}.png");
        if (!File.Exists(hashPath) && File.Exists(legacyHashPath))
            hashPath = legacyHashPath;
        if (!File.Exists(pngPath) && File.Exists(legacyPngPath))
            pngPath = legacyPngPath;

        var hash = Convert.ToHexString(SHA256.HashData(pngBytes));
        var update = string.Equals(
            Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        if (update || !File.Exists(hashPath))
        {
            var baselineHashPath = Path.Combine(baselinesDir, $"{snapshotName}.sha256");
            var baselinePngPath = Path.Combine(baselinesDir, $"{snapshotName}.png");
            File.WriteAllText(baselineHashPath, hash + Environment.NewLine);
            File.WriteAllBytes(baselinePngPath, pngBytes);
            if (File.Exists(actualPath))
                File.Delete(actualPath);
            if (File.Exists(reportPath))
                File.Delete(reportPath);
            return;
        }

        var expected = File.ReadAllText(hashPath).Trim();
        if (string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
            return;

        File.WriteAllBytes(actualPath, pngBytes);
        WriteDiffReport(snapshotName, hashPath, pngPath, actualPath, reportPath, expected, hash);

        var aiHint = TryRunAiReviewHook(pngPath, actualPath, reportPath);
        Assert.Fail(
            $"Snapshot mismatch for '{snapshotName}'. Expected hash={expected}, actual hash={hash}. " +
            $"Wrote actual image to: {actualPath}. Wrote diff report: {reportPath}. {aiHint}" +
            "Set UPDATE_SNAPSHOTS=1 to accept updated snapshots.");
    }

    private static void WriteDiffReport(
        string snapshotName,
        string hashPath,
        string expectedPngPath,
        string actualPngPath,
        string reportPath,
        string expectedHash,
        string actualHash)
    {
        var lines = new[]
        {
            $"# Snapshot Diff: {snapshotName}",
            string.Empty,
            "## Files",
            $"- Expected hash file: `{hashPath}`",
            $"- Expected image: `{expectedPngPath}`",
            $"- Actual image: `{actualPngPath}`",
            string.Empty,
            "## Hashes",
            $"- Expected: `{expectedHash}`",
            $"- Actual: `{actualHash}`",
            string.Empty,
            "## AI Review (Optional)",
            "Run this command to request an automated visual diff summary:",
            $"`SNAPSHOT_AI_REVIEW=1 scripts/ai_snapshot_review.sh \"{expectedPngPath}\" \"{actualPngPath}\" \"{reportPath}\"`",
            string.Empty
        };

        File.WriteAllLines(reportPath, lines);
    }

    private static string TryRunAiReviewHook(string expectedPngPath, string actualPngPath, string reportPath)
    {
        var run = string.Equals(
            Environment.GetEnvironmentVariable("SNAPSHOT_AI_REVIEW"),
            "1",
            StringComparison.OrdinalIgnoreCase);
        if (!run)
            return "Set SNAPSHOT_AI_REVIEW=1 to run AI review hook. ";

        try
        {
            var scriptPath = Path.Combine(GetRepoRoot(), "scripts", "ai_snapshot_review.sh");
            if (!File.Exists(scriptPath))
                return $"AI review hook script not found: {scriptPath}. ";

            var psi = new ProcessStartInfo
            {
                FileName = scriptPath,
                Arguments = $"\"{expectedPngPath}\" \"{actualPngPath}\" \"{reportPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p is null)
                return "AI review hook failed to start. ";

            p.WaitForExit(60_000);
            if (p.ExitCode == 0)
                return $"AI review written to {reportPath}. ";

            var err = p.StandardError.ReadToEnd().Trim();
            return $"AI review hook failed (exit {p.ExitCode}): {err}. ";
        }
        catch (Exception ex)
        {
            return $"AI review hook threw: {ex.Message}. ";
        }
    }

    private static string GetSnapshotsRoot()
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

    private static string GetRepoRoot()
    {
        var snapshots = GetSnapshotsRoot();
        return Directory.GetParent(Directory.GetParent(Directory.GetParent(snapshots)!.FullName)!.FullName)!.FullName;
    }
}
