using System.ComponentModel;

namespace LinuxGate.Models
{
    public class DistroInfo : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Name { get; set; }
        public string Description { get; set; }
        public string ImageUrl { get; set; }
        public string IsoUrl { get; set; }
        public string IsoInstaller { get; set; }
        public string IsoInstallerFileName { get; set; }
        public double SizeInGB { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
