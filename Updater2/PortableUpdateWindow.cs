using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DS4Updater;

// Separate surface and lifetime: never constructs the legacy updater window,
// subscribes to its cleanup, or automatically launches an unverified result.
internal sealed class PortableUpdateWindow : Window
{
    private readonly PortableUpdateRequest request;
    private readonly PortableWorkerRecord worker;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBlock status;
    private readonly Button closeButton, openButton;
    private readonly ProgressBar progress;
    private bool busy = true, applying, successful;

    internal PortableUpdateWindow(PortableUpdateRequest request, PortableWorkerRecord worker)
    {
        this.request = request;
        this.worker = worker;
        Title = "Update portable DS4Windows";
        Width = 560;
        Height = 350;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(11, 20, 30));
        Foreground = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Portable update", FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = request.ReleaseTag, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 6, 0, 18) });
        status = new TextBlock { Text = "Preparing a verified update…", TextWrapping = TextWrapping.Wrap, MinHeight = 52 };
        panel.Children.Add(status);
        progress = new ProgressBar { IsIndeterminate = true, Height = 5, Margin = new Thickness(0, 16, 0, 16) };
        panel.Children.Add(progress);
        panel.Children.Add(new TextBlock { Text = "Your profiles, mappings and portable data stay in place.",
            Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        openButton = new Button { Content = "Open DS4Windows", Visibility = Visibility.Collapsed, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 10, 0) };
        openButton.Click += OpenApplication;
        closeButton = new Button { Content = "Cancel", Padding = new Thickness(12, 7, 12, 7) };
        closeButton.Click += (_, _) => { if (busy) RequestCancellation(); else Close(); };
        buttons.Children.Add(openButton);
        buttons.Children.Add(closeButton);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += RunUpdate;
        Closing += OnClosing;
        Closed += (_, _) => cancellation.Dispose();
    }

    private async void RunUpdate(object sender, RoutedEventArgs args)
    {
        Loaded -= RunUpdate;
        cancellation.CancelAfter(TimeSpan.FromMinutes(15));
        var reporting = new Progress<PortableUpdateProgress>(update =>
        {
            status.Text = update.Message;
            if (update.Applying)
            {
                applying = true;
                closeButton.IsEnabled = false;
            }
        });
        try
        {
            string directory = Path.GetDirectoryName(Environment.ProcessPath);
            using var operations = new PortableUpdateOperations(directory, worker);
            await Task.Run(() => PortableUpdateCoordinator.ExecuteAsync(request, operations, reporting, cancellation.Token));
            successful = true;
            status.Text = "Update complete. Your portable copy is ready.";
            openButton.Visibility = Visibility.Visible;
            // Only our newly created download is removed. The worker/request
            // remain inspectable; no recursive application cleanup is run.
            string archive = Path.Combine(directory, "package.zip");
            try
            {
                PortablePackageTransaction.ValidateNoReparse(archive);
                if (File.Exists(archive)) File.Delete(archive);
            }
            catch { /* A retained download must not invalidate a verified update. */ }
        }
        catch (OperationCanceledException)
        {
            status.Text = "Update canceled before installation. Nothing will be launched.";
        }
        catch (Exception error)
        {
            status.Text = "The update could not complete safely. Nothing was launched.\n\n" + error.Message;
        }
        finally
        {
            busy = false;
            applying = false;
            progress.IsIndeterminate = false;
            closeButton.Content = "Close";
            closeButton.IsEnabled = true;
        }
    }

    private void RequestCancellation()
    {
        if (applying) return;
        cancellation.Cancel();
        closeButton.IsEnabled = false;
        status.Text = "Canceling safely. Please wait…";
    }

    private void OnClosing(object sender, CancelEventArgs args)
    {
        if (!busy) return;
        args.Cancel = true;
        RequestCancellation();
    }

    private async void OpenApplication(object sender, RoutedEventArgs args)
    {
        if (!successful || busy) return;
        openButton.IsEnabled = false;
        try
        {
            await new PortableUpdateProcessGuard().WaitForQuiescenceAsync(request.TargetDirectory, request.LaunchExe,
                timeout: TimeSpan.FromSeconds(1));
            PortableWorkerSession.ValidateLaunchConfiguration(request);
            using var operations = new PortableUpdateOperations(Path.GetDirectoryName(Environment.ProcessPath), worker);
            PortableInstalledIdentity identity = operations.ReadIdentity(request.TargetDirectory, request.LaunchExe);
            if (!ReleaseChannelPolicy.VerifyInstalledIdentity(request.ReleaseTag, identity.FileVersion, identity.ProductVersion, identity.ReleaseTag))
                throw new IOException("The updated application identity changed. Nothing was launched.");
            using Process process = Process.Start(new ProcessStartInfo(Path.Combine(request.TargetDirectory, request.LaunchExe))
            { UseShellExecute = true, WorkingDirectory = request.TargetDirectory }) ?? throw new IOException("DS4Windows could not be launched.");
            Close();
        }
        catch (Exception error) { status.Text = error.Message; openButton.IsEnabled = true; }
    }
}
