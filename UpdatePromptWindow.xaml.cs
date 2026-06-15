using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace Domain_Guardian;

public partial class UpdatePromptWindow : Window
{
    private const string UnknownVersion = "unknown";
    private bool isChangelogExpanded;
    private double collapsedHeight;

    public bool UpdateConfirmed { get; private set; }

    public UpdatePromptWindow(Version latestVersion, Version currentVersion, string? releaseBody = null)
    {
        InitializeComponent();

        VersionInfoText.Text = $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build} \u2192 v{latestVersion.Major}.{latestVersion.Minor}.{latestVersion.Build}";
        MessageText.Text = "A new version of AD Guardian is available. The latest installer will be downloaded and launched automatically.";
        string buildConfig = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? UnknownVersion;
        InstalledVersionText.Text = $"Installed binary: {GetInstalledFileVersion()} (build: {buildConfig})";

        if (!string.IsNullOrWhiteSpace(releaseBody))
        {
            ChangelogText.Text = ParseMarkdownToPlainText(releaseBody);
            ChangelogToggleWrapper.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => collapsedHeight = ActualHeight;
    }

    private static string GetInstalledFileVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion) ? UnknownVersion : informationalVersion;
    }

    /// <summary>
    /// Converts GitHub-flavoured markdown into readable plain text for WPF display.
    /// Strips headers, bullet markers, links, and bold/italic markers while
    /// preserving the structure and readability of the release notes.
    /// </summary>
    internal static string ParseMarkdownToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        StringBuilder sb = new();
        using StringReader reader = new(markdown);
        string? line;
        bool previousWasBlank = false;

        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();

            // Skip horizontal rules (---, ***, ___)
            if (Regex.IsMatch(trimmed, @"^[-*_]{3,}$"))
            {
                continue;
            }

            // Convert markdown headers to plain text with a bullet prefix
            if (trimmed.StartsWith('#'))
            {
                string headerText = Regex.Replace(trimmed, @"^#{1,6}\s*", "").Trim();
                if (!string.IsNullOrWhiteSpace(headerText))
                {
                    if (sb.Length > 0 && !previousWasBlank)
                    {
                        sb.AppendLine();
                    }
                    sb.AppendLine(headerText);
                    previousWasBlank = false;
                }
                continue;
            }

            // Convert list items: keep the bullet but clean up markdown markers
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                string itemText = trimmed[2..].Trim();
                itemText = CleanInlineMarkdown(itemText);
                sb.AppendLine($"  \u2022  {itemText}");
                previousWasBlank = false;
                continue;
            }

            // Numbered list items
            if (Regex.IsMatch(trimmed, @"^\d+\.\s"))
            {
                string itemText = Regex.Replace(trimmed, @"^\d+\.\s*", "").Trim();
                itemText = CleanInlineMarkdown(itemText);
                sb.AppendLine($"    {itemText}");
                previousWasBlank = false;
                continue;
            }

            // Blank lines
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (!previousWasBlank && sb.Length > 0)
                {
                    sb.AppendLine();
                    previousWasBlank = true;
                }
                continue;
            }

            // Regular paragraph text
            string cleaned = CleanInlineMarkdown(trimmed);
            sb.AppendLine(cleaned);
            previousWasBlank = false;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Strips inline markdown formatting: bold (**), italic (*), links ([text](url)),
    /// inline code (`), and image references (![alt](url)).
    /// </summary>
    private static string CleanInlineMarkdown(string text)
    {
        // Strip HTML tags that GitHub release bodies sometimes contain
        text = Regex.Replace(text, @"<[^>]+>", "");
        // Remove image references: ![alt](url) -> alt
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        // Remove links but keep text: [text](url) -> text
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        // Remove bold/italic markers: **text** or *text* -> text (prefer * over _ to avoid snake_case)
        text = Regex.Replace(text, @"\*{1,3}([^*]+)\*{1,3}", "$1");
        // Remove inline code: `text` -> text
        text = Regex.Replace(text, @"`([^`]*)`", "$1");
        return text.Trim();
    }

    private void ChangelogToggleButton_Click(object sender, RoutedEventArgs e)
    {
        isChangelogExpanded = !isChangelogExpanded;

        if (isChangelogExpanded)
        {
            ChangelogPanel.Visibility = Visibility.Visible;
            ChangelogToggleIcon.Text = "\uE70E"; // Chevron up
            ChangelogToggleText.Text = "Hide Changes";
            // Adjust window height to accommodate changelog
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            ChangelogPanel.Visibility = Visibility.Collapsed;
            ChangelogToggleIcon.Text = "\uE76C"; // Chevron right
            ChangelogToggleText.Text = "View Changes";
            SizeToContent = SizeToContent.Manual;
            Height = collapsedHeight > 0 ? collapsedHeight : 240;
        }
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfirmed = true;
        DialogResult = true;
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfirmed = false;
        DialogResult = false;
        Close();
    }
}
