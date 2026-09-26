using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace WpfTestApp
{
    public partial class MainWindow : Window
    {
        private int _clickCount;
        private ObservableCollection<GridItem> _gridItems = new();

        public MainWindow()
        {
            InitializeComponent();
            LoadGridData();
            LoadListData();
        }

        // Buttons tab
        private void ClickMe_Click(object sender, RoutedEventArgs e)
        {
            _clickCount++;
            ClickCountLabel.Text = $"Click count: {_clickCount}";
        }

        private void EnableCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            ConditionalButton.IsEnabled = EnableCheckbox.IsChecked == true;
        }

        // Dialogs tab
        private void ShowMessageBox_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this, "Discard unsaved changes?", "Confirm Discard",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            DialogStatusLabel.Text = $"MessageBox result: {result}";
        }

        private void OpenWpfDialog_Click(object sender, RoutedEventArgs e)
        {
            var nameBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 10) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(nameBox, "DialogNameInput");
            System.Windows.Automation.AutomationProperties.SetName(nameBox, "Your name");

            var dialog = new Window
            {
                Title = "WPF Test Dialog",
                Owner = this,
                Width = 320,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };

            var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) => dialog.DialogResult = true;
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Width = 80 };

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new System.Windows.Controls.TextBlock { Text = "Enter a name:", Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(nameBox);
            root.Children.Add(buttons);
            dialog.Content = root;

            var accepted = dialog.ShowDialog() == true;
            DialogStatusLabel.Text = accepted ? $"WPF dialog result: OK name={nameBox.Text}" : "WPF dialog result: Cancel";
        }

        // Grid tab
        private void LoadGridData()
        {
            var categories = new[] { "Alpha", "Beta", "Gamma", "Delta" };
            var rng = new System.Random(42);
            for (int i = 0; i < 50; i++)
            {
                _gridItems.Add(new GridItem
                {
                    Selected = false,
                    ID = $"ITEM-{i + 1:D3}",
                    Name = $"Test Item {i + 1}",
                    Category = categories[i % categories.Length],
                    Value = $"{rng.Next(1, 1000)}"
                });
            }
            TestDataGrid.ItemsSource = _gridItems;
        }

        private void GridSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _gridItems) item.Selected = true;
            TestDataGrid.Items.Refresh();
            UpdateGridSelection();
        }

        private void GridClear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _gridItems) item.Selected = false;
            TestDataGrid.Items.Refresh();
            UpdateGridSelection();
        }

        private void UpdateGridSelection()
        {
            var count = 0;
            foreach (var item in _gridItems)
                if (item.Selected) count++;
            GridSelectionLabel.Text = $"{count} items selected";
            GridClearButton.IsEnabled = count > 0;
        }

        // Trees tab
        private void LoadListData()
        {
            TestListView.Items.Add(new FileItem { Name = "Document.pdf", Type = "PDF", Size = "2.4 MB" });
            TestListView.Items.Add(new FileItem { Name = "Photo.jpg", Type = "Image", Size = "4.1 MB" });
            TestListView.Items.Add(new FileItem { Name = "Data.csv", Type = "CSV", Size = "156 KB" });
            TestListView.Items.Add(new FileItem { Name = "Report.docx", Type = "Word", Size = "890 KB" });
        }
    }

    public class GridItem : INotifyPropertyChanged
    {
        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
        }
        public string ID { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Value { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        // A data grid row's accessible name comes from ToString.
        public override string ToString() => $"{ID} {Name}";
    }

    public class FileItem
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Size { get; set; } = "";
    }
}
