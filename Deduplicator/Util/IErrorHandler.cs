using System;

namespace Deduplicator.Util;

public interface IErrorHandler
{
    void HandleError(Exception e);
}