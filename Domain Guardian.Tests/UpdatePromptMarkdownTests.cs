using Domain_Guardian;
using Xunit;

namespace Domain_Guardian.Tests;

public class UpdatePromptMarkdownTests
{
    // ── Blockquotes ─────────────────────────────────────────────────────

    [Fact]
    public void ParseMarkdown_Blockquote_IndentedWith4Spaces()
    {
        string input = "> This is a quote";
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        Assert.Contains("    This is a quote", result);
    }

    [Fact]
    public void ParseMarkdown_Blockquote_StripsInlineMarkdown()
    {
        string input = "> **bold** and *italic* text";
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        Assert.Contains("    bold and italic text", result);
    }

    [Fact]
    public void ParseMarkdown_Blockquote_StripsLinks()
    {
        string input = "> See [this page](https://example.com) for details";
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        Assert.Contains("    See this page for details", result);
    }

    [Fact]
    public void ParseMarkdown_Blockquote_StripsHtmlTags()
    {
        string input = "> <strong>Important</strong> note";
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        Assert.Contains("    Important note", result);
    }

    [Fact]
    public void ParseMarkdown_MultipleBlockquotes_AllIndented()
    {
        string input = "> First quote\n> Second quote";
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        Assert.Contains("    First quote", result);
        Assert.Contains("    Second quote", result);
    }

    [Fact]
    public void ParseMarkdown_BlockquoteFollowedByBullet_CorrectFormatting()
    {
        string input = "> A quote\n- A bullet";
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        Assert.Contains("    A quote", result);
        Assert.Contains("  \u2022  A bullet", result);
    }

    // ── Headers ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseMarkdown_H1_StripsHash()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("# Hello World");

        Assert.Contains("Hello World", result);
        Assert.DoesNotContain("#", result);
    }

    [Fact]
    public void ParseMarkdown_H3_StripsAllHashes()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("### Sub heading");

        Assert.Contains("Sub heading", result);
    }

    // ── List items ──────────────────────────────────────────────────────

    [Fact]
    public void ParseMarkdown_BulletDash_ConvertsToBulletChar()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("- Item one");

        Assert.Contains("  \u2022  Item one", result);
    }

    [Fact]
    public void ParseMarkdown_BulletStar_ConvertsToBulletChar()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("* Item two");

        Assert.Contains("  \u2022  Item two", result);
    }

    [Fact]
    public void ParseMarkdown_NumberedItem_Indented()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("1. First item");

        Assert.Contains("    First item", result);
        Assert.DoesNotContain("1.", result);
    }

    // ── Horizontal rules ────────────────────────────────────────────────

    [Fact]
    public void ParseMarkdown_HorizontalRule_Skipped()
    {
        string input = "Before\n---\nAfter";
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        Assert.Contains("Before", result);
        Assert.Contains("After", result);
        Assert.DoesNotContain("---", result);
    }

    // ── Inline markdown ─────────────────────────────────────────────────

    [Fact]
    public void ParseMarkdown_Bold_StripsMarkers()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("This is **bold** text");

        Assert.Contains("This is bold text", result);
    }

    [Fact]
    public void ParseMarkdown_Link_StripsMarkupKeepsText()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("Visit [GitHub](https://github.com) here");

        Assert.Contains("Visit GitHub here", result);
        Assert.DoesNotContain("https://", result);
    }

    [Fact]
    public void ParseMarkdown_Image_StripsMarkupKeepsAlt()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("![Logo](img/logo.png) text");

        Assert.Contains("Logo text", result);
    }

    [Fact]
    public void ParseMarkdown_InlineCode_StripsBackticks()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("Use `npm install` command");

        Assert.Contains("Use npm install command", result);
        Assert.DoesNotContain("`", result);
    }

    [Fact]
    public void ParseMarkdown_HtmlTag_Stripped()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("Line with <br/> break");

        Assert.Contains("Line with", result);
        Assert.Contains("break", result);
        Assert.DoesNotContain("<br/>", result);
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void ParseMarkdown_Null_ReturnsEmpty()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(null!);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ParseMarkdown_EmptyString_ReturnsEmpty()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(string.Empty);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ParseMarkdown_WhitespaceOnly_ReturnsEmpty()
    {
        string result = UpdatePromptWindow.ParseMarkdownToPlainText("   ");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ParseMarkdown_MultipleBlankLines_CollapsesToSingleBlankLine()
    {
        string input = "Line 1\n\n\n\nLine 2";
        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        // Should not have multiple consecutive blank lines
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Contains("Line 1", result);
        Assert.Contains("Line 2", result);
    }

    // ── Full release notes scenario ─────────────────────────────────────

    [Fact]
    public void ParseMarkdown_FullReleaseNotes_FormatsCorrectly()
    {
        string input = @"## What's Changed

### New Features
- **Update prompt changelog** — view changes before updating.

> Thanks to @contributor for the suggestion!

### Bug Fixes
- Fixed log error messages
- Improved [blockquote handling](https://github.com)

---

Full details in the attached log file.";

        string result = UpdatePromptWindow.ParseMarkdownToPlainText(input);

        Assert.Contains("What's Changed", result);
        Assert.Contains("New Features", result);
        Assert.Contains("  \u2022  Update prompt changelog", result);
        Assert.Contains("    Thanks to @contributor for the suggestion!", result);
        Assert.Contains("  \u2022  Fixed log error messages", result);
        Assert.Contains("  \u2022  Improved blockquote handling", result);
        Assert.DoesNotContain("---", result);
        Assert.DoesNotContain("[" , result.Replace("Thanks", "")); // links stripped
        Assert.Contains("Full details in the attached log file.", result);
    }
}
