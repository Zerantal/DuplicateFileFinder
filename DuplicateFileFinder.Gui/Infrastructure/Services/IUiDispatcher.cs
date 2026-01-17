// DuplicateFileFinder.Gui/Infrastructure/Services/IUiDispatcher.cs
namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public interface IUiDispatcher
{
    void Post(Action action);
}

