using System.ComponentModel;
using System.Runtime.CompilerServices;
using DuplicateFileFinder.Properties;

namespace DuplicateFileFinder.Models
{
    public class BaseObjectModel : INotifyPropertyChanged
    {
        
        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
