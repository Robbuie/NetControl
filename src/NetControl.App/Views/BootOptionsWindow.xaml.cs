using System.Windows;
using NetControl.Core.Dhcp;

namespace NetControl.App.Views;

/// <summary>
/// The five boot option fields. Parsing and every rule live in <see cref="BootOptions.TryCreate"/>,
/// where the tests are; this fills the boxes, shows the problems, and hands back a valid set.
/// </summary>
public partial class BootOptionsWindow : Window
{
    public BootOptionsWindow(BootOptions current)
    {
        ArgumentNullException.ThrowIfNull(current);

        InitializeComponent();

        NextServerBox.Text = current.NextServer?.ToString() ?? string.Empty;
        TftpServerBox.Text = current.TftpServerName ?? string.Empty;
        BootFileBox.Text = current.BootFileName ?? string.Empty;
        DomainBox.Text = current.DomainName ?? string.Empty;
        DnsBox.Text = string.Join(", ", current.DnsServers);

        Loaded += (_, _) => NextServerBox.Focus();
    }

    /// <summary>The validated options, once Save has succeeded.</summary>
    public BootOptions Result { get; private set; } = BootOptions.None;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!BootOptions.TryCreate(
                NextServerBox.Text,
                TftpServerBox.Text,
                BootFileBox.Text,
                DomainBox.Text,
                DnsBox.Text,
                out BootOptions options,
                out IReadOnlyList<string> problems))
        {
            ProblemText.Text = string.Join(Environment.NewLine, problems);
            ProblemPanel.Visibility = Visibility.Visible;
            return;
        }

        Result = options;
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        NextServerBox.Text = string.Empty;
        TftpServerBox.Text = string.Empty;
        BootFileBox.Text = string.Empty;
        DomainBox.Text = string.Empty;
        DnsBox.Text = string.Empty;
        ProblemPanel.Visibility = Visibility.Collapsed;
    }
}
