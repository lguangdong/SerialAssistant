using SerialAssistant.ViewModels;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace SerialAssistant
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();
        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            _vm.DisplayAppend += OnDisplayAppend;
            _vm.DisplayReplace += OnDisplayReplace;
            // 这个页面内容较多，进入时适当放大窗口
            if (Window.GetWindow(this) is Window window)
            {
                if (window.Width < 400) window.Width = 400;
                if (window.Height < 640) window.Height = 760;
            }
        }

        private void OnDisplayAppend(string line) => ReceiveBox.AppendText(line);
        private void OnDisplayReplace(string text) => ReceiveBox.Text = text;

        /// <summary>接收区自动滚动到底部</summary>
        private void OnReceiveTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_vm.AutoScroll && sender is TextBox box)
                box.ScrollToEnd();
        }

        /// <summary>双击快捷指令 → 立即发送</summary>
        private void OnQuickCommandDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (QuickList.SelectedItem is string command)
                _vm.SendQuickCommandCommand.Execute(command);
        }

        protected override void OnClosed(EventArgs e)
        {
            _vm.DisplayAppend -= OnDisplayAppend;
            _vm.DisplayReplace -= OnDisplayReplace;
            _vm.Dispose();
            base.OnClosed(e);
        }
    }
}