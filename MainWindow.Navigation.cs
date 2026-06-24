// MainWindow partial class - Navigation functionality
// Extracted from MainWindow.xaml.cs during partial class refactoring.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Navigation;
using System.Windows.Threading;
using Domain_Guardian;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AdHealthMonitor;

public partial class MainWindow
{
    private void LoadSettings()
    {
        PersistedAppSettings settings = appStateStore.LoadSettings();
        domainControllers = settings.DomainControllers;
        recipientEmail = settings.RecipientEmail;
        testDnsCheck = settings.TestDnsCheck;
        testReplication = settings.TestReplication;
        testTimeSkew = settings.TestTimeSkew;
        testLdapBind = settings.TestLdapBind;
        testCertDhcp = settings.TestCertDhcp;
        testSmbLdapSigning = settings.TestSmbLdapSigning;
        sendEmailManual = settings.SendEmailManual;
        sendEmailScheduled = settings.SendEmailScheduled;
        RefreshDashboard();
    }

    private void SaveSettings()
    {
        appStateStore.SaveSettings(new PersistedAppSettings
        {
            DomainControllers = domainControllers,
            RecipientEmail = recipientEmail,
            TestDnsCheck = testDnsCheck,
            TestReplication = testReplication,
            TestTimeSkew = testTimeSkew,
            TestLdapBind = testLdapBind,
            TestCertDhcp = testCertDhcp,
            TestSmbLdapSigning = testSmbLdapSigning,
            SendEmailManual = sendEmailManual,
            SendEmailScheduled = sendEmailScheduled
        });
    }

    private void HomeRunTests_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        NavigateToSection(1);
        RunButton_Click(sender, e);
    }

    private void HomeViewFindings_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        NavigateToSection(2);
    }

    private void HomeViewHistory_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        NavigateToSection(4);
    }

    // Pre-allocated objects for HomeCard hover effects — reused instead of allocating on every mouse enter.
    // DropShadowEffect is frozen so it can be safely shared across the 3 Home cards.
    private static readonly DropShadowEffect HomeCardHoverShadow;
    private static readonly TranslateTransform HomeCardHoverTransform = new(0, -3);
    private static readonly Point HomeCardRenderOrigin = new(0.5, 0.5);

    // Pre-allocated easing functions for sidebar and group-toggle animations — reused per click.
    private static readonly CircleEase SidebarEasing = new() { EasingMode = EasingMode.EaseInOut };
    private static readonly QuadraticEase GroupToggleEasing = new() { EasingMode = EasingMode.EaseInOut };

    static MainWindow()
    {
        HomeCardHoverShadow = new DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 4,
            Opacity = 0.25,
            Color = Colors.Black
        };
        HomeCardHoverShadow.Freeze();
        HomeCardHoverTransform.Freeze();
        SidebarEasing.Freeze();
        GroupToggleEasing.Freeze();
    }

    private void HomeCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border card && card.Parent is Grid parentGrid &&
            parentGrid.Children.Count > 0 && parentGrid.Children[0] is Border overlay)
        {
            overlay.Opacity = 0.08;
            card.Effect = HomeCardHoverShadow;
            parentGrid.RenderTransform = HomeCardHoverTransform;
            parentGrid.RenderTransformOrigin = HomeCardRenderOrigin;
        }
    }

    private void HomeCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border card && card.Parent is Grid parentGrid &&
            parentGrid.Children.Count > 0 && parentGrid.Children[0] is Border overlay)
        {
            overlay.Opacity = 0;
            card.Effect = null;
            parentGrid.RenderTransform = null;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection(7);
    }

    private void LoadSettingsIntoPage()
    {
        SettingsDcTextBox.Text = domainControllers;
        SettingsEmailTextBox.Text = recipientEmail;
        SettingsChkDns.IsChecked = testDnsCheck;
        SettingsChkReplication.IsChecked = testReplication;
        SettingsChkTimeSkew.IsChecked = testTimeSkew;
        SettingsChkLdapBind.IsChecked = testLdapBind;
        SettingsChkCertDhcp.IsChecked = testCertDhcp;
        SettingsChkSmbSigning.IsChecked = testSmbLdapSigning;
        SettingsChkEmailManual.IsChecked = sendEmailManual;
        SettingsChkEmailScheduled.IsChecked = sendEmailScheduled;
    }

    internal void SettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        domainControllers = SettingsDcTextBox.Text.Trim();
        recipientEmail = SettingsEmailTextBox.Text.Trim();
        testDnsCheck = SettingsChkDns.IsChecked ?? false;
        testReplication = SettingsChkReplication.IsChecked ?? true;
        testTimeSkew = SettingsChkTimeSkew.IsChecked ?? false;
        testLdapBind = SettingsChkLdapBind.IsChecked ?? false;
        testCertDhcp = SettingsChkCertDhcp.IsChecked ?? false;
        testSmbLdapSigning = SettingsChkSmbSigning.IsChecked ?? false;
        sendEmailManual = SettingsChkEmailManual.IsChecked ?? true;
        sendEmailScheduled = SettingsChkEmailScheduled.IsChecked ?? true;
        try
        {
            SaveSettings();
            RefreshDashboard();
            new SuccessNotification("Settings Saved", "Your settings have been saved successfully.") { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            new SuccessNotification("Settings Error", $"Failed to save settings:\n{ex.Message}", isError: true) { Owner = this }.ShowDialog();
        }
    }

    internal void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearchFilter();

    internal void SearchButton_Click(object sender, RoutedEventArgs e) => ApplySearchFilter();

    internal void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SearchPlaceholder.Visibility = Visibility.Collapsed;
    }

    internal void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SearchPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void NavigateToSection(int index)
    {
        if (index < 0 || index >= MainTabControl.Items.Count)
        {
            return;
        }

        if (MainTabControl.SelectedIndex != index)
        {
            MainTabControl.SelectedIndex = index;
        }
    }

    private void NavSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
            index >= 0 &&
            index < MainTabControl.Items.Count)
        {
            NavigateToSection(index);
        }
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && toggle.Tag is string panelName)
        {
            if (FindName(panelName) is FrameworkElement panel)
            {
                bool expanded = toggle.IsChecked == true;
                panel.Visibility = Visibility.Visible;

                if (expanded)
                {
                    DoubleAnimation expandAnim = new()
                    {
                        From = 0,
                        To = 500,
                        Duration = TimeSpan.FromMilliseconds(200),
                        EasingFunction = GroupToggleEasing
                    };
                    panel.BeginAnimation(FrameworkElement.MaxHeightProperty, expandAnim);
                }
                else
                {
                    double startH = panel.ActualHeight > 0 ? panel.ActualHeight : 100;
                    DoubleAnimation collapseAnim = new()
                    {
                        From = startH,
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(200),
                        EasingFunction = GroupToggleEasing
                    };
                    panel.BeginAnimation(FrameworkElement.MaxHeightProperty, collapseAnim);
                }
            }

            // Update arrow direction
            string arrow = toggle.IsChecked == true ? "\uE70D" : "\uE76C";
            if (toggle.Name == "MonitoringToggle" && MonitoringArrow != null)
                MonitoringArrow.Text = arrow;
            else if (toggle.Name == "DataToggle" && DataArrow != null)
                DataArrow.Text = arrow;
        }
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        int selectedIndex = MainTabControl.SelectedIndex;
        UpdateNavigationState();
        EnsurePageBindings(selectedIndex);
        if (selectedIndex == 1)
        {
            EnsureHealthDetailsTextLoaded();
        }
        else if (selectedIndex == 7)
        {
            LoadSettingsIntoPage();
        }
        else if (selectedIndex == 5)
        {
            if (logsTextPending)
            {
                _ = LoadLogsTabContentAsync();
            }
            else if (LogsListBox.ItemsSource == null && !string.IsNullOrWhiteSpace(latestLogsText))
            {
                RefreshLogsWorkspace();
            }
            else if (LogsListBox.ItemsSource == null)
            {
                // Show empty state until filters are used
                LogsFileNameText.Text = "Select filters above to view log content";
            }
        }
        else if (selectedIndex == 6)
        {
            UpdateSecurityGrid();
        }
        else if (selectedIndex == 8)
        {
            RefreshSchedulerTaskList();
        }

        if (MainTabControl.SelectedContent is UIElement content)
        {
            content.BeginAnimation(UIElement.OpacityProperty, null);
            content.Opacity = 1.0;
        }

        if (selectedIndex == 5)
        {
            RefreshLogsWorkspace();
        }
    }

    private void MainTabControl_Loaded(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 0;
        UpdateNavigationState();
    }

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        isSidebarCollapsed = !isSidebarCollapsed;
        SidebarToggleButton.IsChecked = isSidebarCollapsed;
        SidebarToggleButton.ToolTip = isSidebarCollapsed ? "Expand sidebar" : "Collapse sidebar";
        double targetWidth = isSidebarCollapsed ? 74 : 220;

        SidebarPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
        double fromW = SidebarPanel.ActualWidth > 0 ? SidebarPanel.ActualWidth : (isSidebarCollapsed ? 220 : 74);

        DoubleAnimation widthAnim = new()
        {
            From = fromW,
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = SidebarEasing
        };
        SidebarPanel.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);

        double targetOpacity = isSidebarCollapsed ? 0.0 : 1.0;
        void AnimateLabel(TextBlock label)
        {
            label.BeginAnimation(UIElement.OpacityProperty, null);
            double fromO = label.Opacity;
            DoubleAnimation fadeAnim = new()
            {
                From = fromO,
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = SidebarEasing
            };
            label.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }

        SidebarHeaderContent.BeginAnimation(UIElement.OpacityProperty, null);
        double fromO2 = SidebarHeaderContent.Opacity;
        DoubleAnimation fadeContent = new()
        {
            From = fromO2,
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = SidebarEasing
        };
        SidebarHeaderContent.BeginAnimation(UIElement.OpacityProperty, fadeContent);
        AnimateLabel(HomeNavLabel);
        AnimateLabel(HealthNavLabel);
        AnimateLabel(FindingsNavLabel);
        AnimateLabel(InfrastructureNavLabel);
        AnimateLabel(HistoryNavLabel);
        AnimateLabel(LogsNavLabel);
        AnimateLabel(SecurityNavLabel);
        AnimateLabel(SchedulerNavLabel);
        AnimateLabel(SettingsNavLabel);
        if (MonitoringNavLabel != null) AnimateLabel(MonitoringNavLabel);
        if (MonitoringArrow != null) AnimateLabel(MonitoringArrow);
        if (DataNavLabel != null) AnimateLabel(DataNavLabel);
        if (DataArrow != null) AnimateLabel(DataArrow);
    }

    private void UpdateNavigationState()
    {
        UpdateNavigationState(MainTabControl.SelectedIndex);
    }

    private static void SetNavButtonState(Button button, bool isActive)
    {
        button.Background = isActive ? ActiveNavBgBrush : Brushes.Transparent;
        button.Foreground = isActive ? Brushes.White : InactiveNavFgBrush;
    }

}
