#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Deduplicator.Common;

public class BindableBase : INotifyPropertyChanged, INotifyDataErrorInfo
{
    #region INotifyPropertyChanged Implementation

    public event PropertyChangedEventHandler? PropertyChanged;

    // ReSharper disable once UnusedMember.Global
    protected virtual bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;

        storage = value;
        RaisePropertyChanged(propertyName);

        return true;
    }

    // ReSharper disable once UnusedMember.Global
    protected virtual bool SetProperty<T>(ref T storage, T value, Action onChanged, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;

        storage = value;
        onChanged();
        RaisePropertyChanged(propertyName);

        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
    }

    protected virtual void OnPropertyChanged(PropertyChangedEventArgs args)
    {
        PropertyChanged?.Invoke(this, args);
    }

    #endregion

    #region INotifyDataErrorInfo Implemtation

    private readonly Dictionary<string, IEnumerable> _errors = new();
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public bool HasErrors => _errors.Any();

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName) || _errors.ContainsKey(propertyName))
            return Array.Empty<string>();

        return _errors[propertyName];
    }

    protected void NotifyErrorsChanged(string? propertyName, string error)
    {
        if (propertyName == null)
            return;

        if (ErrorsChanged == null)
        {
            return;
        }

        if (!_errors.ContainsKey(propertyName))
        {
            _errors.Add(propertyName, new[] { error });
        }

        ErrorsChanged(this, new DataErrorsChangedEventArgs(propertyName));
    }

    // ReSharper disable once UnusedMember.Global
    protected void ErrorIfEmpty(string s, [CallerMemberName] string? propertyName = null)
    {
        if (propertyName == null)
            return;

        if (string.IsNullOrEmpty(s))
        {
            NotifyErrorsChanged(propertyName, "Required");
        }
        else
        {
            _errors.Remove(propertyName);
        }
    }

    // ReSharper disable once UnusedMember.Global
    protected void ErrorIfDirectoryNotFound(string path, [CallerMemberName] string? propertyName = null)
    {
        if (propertyName == null)
            return;

        if (Directory.Exists(path))
        {
            _errors.Remove(propertyName);
        }
        else
        {
            NotifyErrorsChanged(propertyName, "Directory not found");
        }
    }

    // ReSharper disable once UnusedMember.Global
    protected void ErrorIfFileNotFound(string path, [CallerMemberName] string? propertyName = null)
    {
        if (propertyName == null)
            return;

        if (File.Exists(path))
        {
            _errors.Remove(propertyName);
        }
        else
        {
            NotifyErrorsChanged(propertyName, "File not found");
        }
    }

    #endregion
}