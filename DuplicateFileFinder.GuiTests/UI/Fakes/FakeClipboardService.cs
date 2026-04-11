using System.Collections.Generic;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Infrastructure.Services;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeClipboardService : IClipboardService
{
    public List<string> CopiedTexts { get; } = [];

    public Task SetTextAsync(string text)
    {
        CopiedTexts.Add(text);
        return Task.CompletedTask;
    }
}
