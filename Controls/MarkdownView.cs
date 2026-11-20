using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Neo.Markdig.Xaml;
using FontFamily = System.Windows.Media.FontFamily;

namespace WinFlow.Controls;

/// <summary>
/// Renders markdown as a native WPF FlowDocument (Markdig → Neo.Markdig.Xaml).
/// Text stays selectable and theme-aware: foreground/background come from
/// DynamicResource lookups, so light/dark/high-contrast switches apply in place.
/// Falls back to plain wrapped text when parsing fails — never a blank pane.
/// </summary>
public class MarkdownView : FlowDocumentScrollViewer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseXamlSupportedExtensions().Build();

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownView()
    {
        IsToolBarVisible = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = NewDocument();
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MarkdownView)d).Render((string?)e.NewValue ?? string.Empty);

    private static FlowDocument NewDocument()
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(0),
            LineHeight = 22,
            IsColumnWidthFlexible = false,
            ColumnWidth = double.PositiveInfinity,
        };
        doc.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        return doc;
    }

    private void Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            Document = NewDocument();
            return;
        }

        FlowDocument doc;
        try
        {
            doc = MarkdownXaml.ToFlowDocument(markdown, Pipeline);
        }
        catch
        {
            // Parsing failed: show the raw text rather than nothing.
            doc = NewDocument();
            doc.Blocks.Add(new Paragraph(new Run(markdown)));
            Document = doc;
            return;
        }

        doc.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        doc.FontSize = 14;
        doc.PagePadding = new Thickness(0);
        doc.LineHeight = 22;
        doc.ColumnWidth = double.PositiveInfinity;
        doc.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");

        RestyleBlocks(doc.Blocks);
        Document = doc;
    }

    /// <summary>
    /// The stock renderer hardcodes black text and grey code backgrounds.
    /// Replace those local values with theme-aware ones, block by block.
    /// </summary>
    private static void RestyleBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Section section:
                    section.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
                    section.BorderThickness = new Thickness(3, 0, 0, 0);
                    section.Padding = new Thickness(10, 2, 0, 2);
                    section.Margin = new Thickness(0, 6, 0, 6);
                    section.SetResourceReference(Block.BorderBrushProperty, "SummaryCardBorderBrush");
                    RestyleBlocks(section.Blocks);
                    break;

                case List list:
                    list.Margin = new Thickness(0, 4, 0, 4);
                    list.Padding = new Thickness(18, 0, 0, 0);
                    foreach (var item in list.ListItems)
                    {
                        RestyleBlocks(item.Blocks);
                    }
                    break;

                case Paragraph p:
                    RestyleParagraph(p);
                    break;
            }
        }
    }

    private static void RestyleParagraph(Paragraph p)
    {
        bool hasLocalStyle = p.ReadLocalValue(FrameworkContentElement.StyleProperty) is Style;
        bool isCode = false;

        foreach (var inline in p.Inlines)
        {
            if (inline is Span span && span.ReadLocalValue(FrameworkContentElement.StyleProperty) is Style)
            {
                // Inline code / fenced block: theme the grey chip.
                isCode = true;
                span.ClearValue(FrameworkContentElement.StyleProperty);
                span.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                span.FontSize = 13;
                span.SetResourceReference(TextElement.BackgroundProperty, "KeycapBackgroundBrush");
                span.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            }
            else if (inline is Hyperlink link)
            {
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            }
            else if (inline is Span container)
            {
                foreach (var nested in container.Inlines)
                {
                    if (nested is Hyperlink nestedLink)
                    {
                        nestedLink.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                    }
                }
            }
        }

        if (hasLocalStyle)
        {
            var old = (Style)p.GetValue(FrameworkContentElement.StyleProperty);
            p.ClearValue(FrameworkContentElement.StyleProperty);
            p.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");

            double oldSize = old.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property == TextElement.FontSizeProperty)?.Value is double d ? d : 14;

            if (isCode && old.Setters.OfType<Setter>().Any(s => s.Property == TextElement.BackgroundProperty))
            {
                // Fenced code block.
                p.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                p.FontSize = 13;
                p.Padding = new Thickness(10, 8, 10, 8);
                p.Margin = new Thickness(0, 6, 0, 6);
                p.SetResourceReference(TextElement.BackgroundProperty, "KeycapBackgroundBrush");
            }
            else
            {
                // Heading: the stock sizes (42/28/…) are far too loud for a notes pane.
                p.FontSize = oldSize switch
                {
                    >= 40 => 20,
                    >= 28 => 17,
                    >= 22 => 15.5,
                    _ => 14.5,
                };
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, 10, 0, 4);
            }
        }
        else
        {
            p.Margin = new Thickness(0, 0, 0, 8);
        }
    }

    /// <summary>Approximate reading time label, e.g. "1 MIN READ" (200 wpm).</summary>
    public static string ReadingTime(string text)
    {
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int minutes = Math.Max(1, (int)Math.Ceiling(words / 200.0));
        return $"{minutes} MIN READ";
    }
}
