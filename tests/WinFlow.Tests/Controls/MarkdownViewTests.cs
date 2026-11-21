using System.Windows;
using System.Windows.Documents;
using WinFlow.Controls;
using Xunit;

namespace WinFlow.Tests.Controls;

/// <summary>
/// MarkdownView touches WPF visuals, so every test runs on a dedicated STA thread.
/// </summary>
public class MarkdownViewTests
{
    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result;
    }

    [Fact]
    public void Markdown_RendersHeadingsListsAndCode_AsFlowBlocks()
    {
        int blocks = OnSta(() =>
        {
            var view = new MarkdownView
            {
                Markdown = "# Topic\n\nBody **bold**.\n\n- one\n- two\n\n```\ncode block\n```"
            };
            Assert.NotNull(view.Document);
            Assert.Contains(view.Document.Blocks, b => b is List);
            return view.Document.Blocks.Count;
        });
        Assert.True(blocks >= 4);
    }

    [Fact]
    public void Headings_AreRestyled_AwayFromStockBlackAndHugeSizes()
    {
        OnSta(() =>
        {
            var view = new MarkdownView { Markdown = "# Big heading\n\ntext" };
            var heading = view.Document.Blocks.OfType<Paragraph>().First();
            // Stock renderer hardcodes 42pt black; ours must shrink and use a
            // dynamic theme reference instead of a hardcoded brush.
            Assert.True(heading.FontSize <= 20);
            var fg = heading.ReadLocalValue(TextElement.ForegroundProperty);
            Assert.NotEqual(DependencyProperty.UnsetValue, fg);
            Assert.IsNotType<System.Windows.Media.SolidColorBrush>(fg);
            return true;
        });
    }

    [Fact]
    public void Empty_Markdown_YieldsEmptyDocument()
    {
        OnSta(() =>
        {
            var view = new MarkdownView { Markdown = "   " };
            Assert.Empty(view.Document.Blocks);
            return true;
        });
    }

    [Theory]
    [InlineData("", "1 MIN READ")]
    [InlineData("one two three", "1 MIN READ")]
    [InlineData("word word word word word", "1 MIN READ")]
    public void ReadingTime_RoundsUp_AndFloorsAtOneMinute(string text, string expected)
    {
        Assert.Equal(expected, MarkdownView.ReadingTime(text));
    }

    [Fact]
    public void ReadingTime_LongText_CountsMinutes()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 450));
        Assert.Equal("3 MIN READ", MarkdownView.ReadingTime(text));
    }
}
