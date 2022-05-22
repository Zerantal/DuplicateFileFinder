using System;

namespace DuplicateFileFinder.Util;

public interface IErrorHandler
{
    void HandleError(Exception e);
}