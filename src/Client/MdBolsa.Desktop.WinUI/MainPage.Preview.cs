using MdBolsa.Contracts;
using MdBolsa.Core.Attachments;
using MdBolsa.Core.Links;
using MdBolsa.Core.Markdown;
using Windows.Storage;
using Windows.Storage.Streams;
using MdBolsa.Core.Search;
using MdBolsa.Core.Tags;
using MdBolsa.Core.Sync;
using MdBolsa.Core.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using ShapeRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.System;
using PathShape = Microsoft.UI.Xaml.Shapes.Path;

namespace MdBolsa_Desktop_WinUI;

// The live preview pane: Markdown rendered beside the source, and its resizable
// divider. Part of MainPage; see MainPage.xaml.cs.
public sealed partial class MainPage
{
    // --- Live preview (docs/decisions/0013) --------------------------------

    // A read-only pane beside the source, rendering the open note as Markdown. It is
    // a projection and never the source of truth: saving writes the TextBox, exactly
    // as it did before this pane existed, and the editor's crash-prone paths are
    // untouched. See 0013 for why the editor was not replaced by a rendered view.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _previewTimer;

    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(400);

    private void OnPreviewClicked(object sender, RoutedEventArgs e) => TogglePreview();

    private void OnTogglePreviewAccelerator(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        TogglePreview();

    private void TogglePreview()
    {
        var showing = PreviewHost.Visibility == Visibility.Visible;
        PreviewHost.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;

        // The divider only exists while there is something to divide.
        PreviewSplitter.Visibility = PreviewHost.Visibility;
        PreviewRibbonButton.Opacity = showing ? 0.55 : 1;

        if (showing) return;

        // Applied on every open, not once at startup: a window restored to a
        // narrower screen than it was left on should clamp rather than push the
        // editor off the edge.
        ApplyPreviewWidth(AppSession.PreviewWidth ?? DefaultPreviewWidth);

        RenderPreview(ReadCurrentNoteContent() ?? string.Empty);
    }

    // Re-rendering is debounced onto the dispatcher. Reading the text on every
    // keystroke and rebuilding the visual tree from a text-changed handler is exactly
    // the pattern that crashes this runtime, so the preview is allowed to be a
    // fraction of a second behind the caret - which, at 400ms, nobody can see.
    private void SchedulePreviewRefresh()
    {
        if (PreviewHost.Visibility != Visibility.Visible) return;

        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RenderPreview(string markdown)    {
        PreviewPanel.Children.Clear();

        var blocks = MarkdownParser.Parse(markdown);
        if (blocks.Count == 0)
        {
            PreviewPanel.Children.Add(new TextBlock
            {
                Text = "Nothing to preview.",
                Opacity = 0.6,
                FontSize = 12,
            });
            return;
        }

        foreach (var block in blocks)
        {
            var element = BuildBlock(block);
            if (element is not null) PreviewPanel.Children.Add(element);
        }
    }

    /// <summary>
    /// A code block - or, for ```mermaid, a drawn flowchart.
    ///
    /// The rule is the same one the whole preview follows (0013): if it cannot be
    /// rendered honestly, it is shown as the code it is, and it says why. A reader
    /// who sees a code block *knows* the diagram did not draw; a reader who sees a
    /// half-drawn diagram is being lied to and has no way to tell.
    /// </summary>
    private static UIElement BuildCodeBlock(Block.Code code)
    {
        var isMermaid = string.Equals(code.Language, "mermaid", StringComparison.OrdinalIgnoreCase);

        // Declared up front and filled in only for mermaid. Short-circuiting the
        // call with `isMermaid && TryParse(...)` would leave the `out` variables
        // unassigned on the other branch, and a note full of ```csharp must not pay
        // for a parse it will never use.
        var chart = MdBolsa.Core.Diagrams.Flowchart.Empty;
        MdBolsa.Core.Diagrams.FlowchartParseError? error = null;

        if (isMermaid)
        {
            MdBolsa.Core.Diagrams.FlowchartParser.TryParse(code.Text, out chart, out error);
        }

        if (isMermaid && error is null && chart.NodeCount > 0)
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(FlowchartPresenter.Create(chart));

            // The diagram is derived from the note; the text is still what is on
            // disk, and "how do I edit that?" is the first question anybody has
            // about a picture inside a text editor.
            //
            // A button that toggles a panel rather than an Expander. Not an
            // aesthetic choice: the Known Issues in architecture.md are a list of
            // control types this runtime has crashed on, and Expander is a type no
            // other part of this app uses. Introducing one to save a line is a bad
            // trade when the same effect is two lines of Visibility - the pattern
            // the sidebar panels already use.
            var sourcePanel = new Border
            {
                Visibility = Visibility.Collapsed,
                Child = BuildCodeSurface(code.Text),
            };

            var toggle = new Button
            {
                Content = "Show the diagram's code",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
            };

            toggle.Click += (_, _) =>
            {
                var showing = sourcePanel.Visibility == Visibility.Visible;
                sourcePanel.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
                toggle.Content = showing ? "Show the diagram's code" : "Hide the diagram's code";
            };

            panel.Children.Add(toggle);
            panel.Children.Add(sourcePanel);

            foreach (var warning in chart.Warnings) panel.Children.Add(BuildNotice(warning));

            return panel;
        }

        var fallback = new StackPanel { Spacing = 4 };
        fallback.Children.Add(BuildCodeSurface(code.Text));

        // Only for mermaid, and only when it failed: a note full of ```csharp must
        // not collect explanations about its own code fences.
        if (isMermaid)
        {
            fallback.Children.Add(BuildNotice(
                error?.ToString() ?? "This diagram could not be drawn, so it is shown as code."));
        }

        return fallback;
    }

    private static UIElement BuildNotice(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.8,
        Foreground = new SolidColorBrush(Presentation.Warning),
    };

    private static UIElement BuildCodeSurface(string text) => new Border
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 10, 12, 10),
        Background = (Brush)Application.Current.Resources["CodeBackgroundBrush"],
        Child = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        },
    };

    private static UIElement? BuildBlock(Block block) => block switch
    {        Block.Heading heading => new TextBlock
        {
            Text = Rendered(heading.Inlines),
            FontSize = HeadingSize(heading.Level),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        },

        Block.Paragraph paragraph => new TextBlock
        {
            Text = Rendered(paragraph.Inlines),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
        },

        Block.List list => BuildList(list),

        Block.Quote quote => BuildQuote(quote),

        Block.Code code => BuildCodeBlock(code),

        Block.Rule => new ShapeRectangle
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Fill = (Brush)Application.Current.Resources["DividerBrush"],
        },

        Block.Image image => BuildImage(image),

        _ => null,
    };

    // Draws the image, or says why it cannot.
    //
    // Resolution is deliberately narrow: a bare name is looked for in the
    // attachments folder, and a path is resolved inside the vault and nowhere else.
    // A preview that would follow a path out of the vault, or fetch a URL, is a
    // preview with side effects - and a missing image says so rather than leaving a
    // gap, because a silent gap is the failure mode people describe as "it just does
    // not show sometimes".
    private static FrameworkElement BuildImage(Block.Image image)
    {
        var source = image.Source;
        var path = ResolveVaultPath(source);

        if (path is null)
        {
            return MissingAttachment($"No attachment called {source} in the vault.");
        }

        // SVG is a document, not a bitmap, and the image control cannot draw one.
        if (source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return MissingAttachment($"{source} is an SVG. The preview shows a link, not the drawing.");
        }

        try
        {
            // Asynchronously, from a random-access stream: a decoded 4000x3000 PNG is
            // ~48 MB in memory, and SetSource on a byte array would hold the whole
            // file *and* the decode. A broken file throws on decode rather than
            // rendering nothing, so the continuation is where the failure is reported.
            var control = new Image
            {
                MaxHeight = 420,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            _ = LoadImageAsync(control, path);
            return control;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return MissingAttachment($"{source} could not be opened: {ex.Message}");
        }
    }

    private static async Task LoadImageAsync(Image control, string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);

            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(stream);
            control.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Reported in place of the picture. A decode failure that is swallowed is
            // indistinguishable from a broken reference, and the first one is a bug.
            control.Visibility = Visibility.Collapsed;
        }
    }

    private static FrameworkElement MissingAttachment(string message) => new StackPanel
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = "missing attachment", FontSize = 11, Opacity = 0.6 },
            new TextBlock { Text = message, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap },
        },
    };

    // Inside the vault, and nowhere else. A name with no separator is an attachment;
    // a path is taken relative to the vault root. Anything that climbs out with ".."
    // is refused rather than normalised - a note that references ../../secrets is a
    // bug, and quietly serving it would be worse than saying no.
    private static string? ResolveVaultPath(string source)
    {
        if (AppSession.VaultPath is null) return null;
        if (source.Contains("://", StringComparison.Ordinal)) return null;
        if (source.Split('/').Any(segment => segment == "..")) return null;

        var candidate = source.Contains('/')
            ? Path.Combine(AppSession.VaultPath, source.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(
                AppSession.VaultPath,
                MdBolsa.Core.Attachments.AttachmentRules.FolderPath,
                source);

        return File.Exists(candidate) ? candidate : null;
    }

    private static Border BuildQuote(Block.Quote quote)
    {
        var inner = new StackPanel { Spacing = 8 };
        foreach (var child in quote.Blocks.Select(BuildBlock).OfType<UIElement>())
        {
            inner.Children.Add(child);
        }

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["DividerBrush"],
            Padding = new Thickness(12, 0, 0, 0),
            Child = inner,
        };
    }

    private static UIElement BuildList(Block.List list)
    {
        var panel = new StackPanel { Spacing = 4 };

        var number = list.Start;
        foreach (var item in list.Items)
        {
            panel.Children.Add(new TextBlock
            {
                // The marker is text, not a bullet glyph: a numbered list that starts
                // at 3 has to show "3.", and a bullet list shows "-", so the reader
                // sees what the file says.
                Text = $"{Marker(list, number++)}  {Rendered(item)}",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
            });
        }

        return panel;
    }

    private static string Marker(Block.List list, int number) =>
        list.Ordered ? $"{number}." : "-";

    private static double HeadingSize(int level) => level switch
    {
        1 => 22,
        2 => 18,
        3 => 15,
        _ => 13,
    };

    // The text a reader sees. A link shows its label, a wiki link its alias if it has
    // one, emphasis and code their contents - the markers never reach the reader,
    // which is the rule that keeps a preview from looking broken.
    private static string Rendered(IReadOnlyList<Inline> inlines) =>
        string.Concat(inlines.Select(inline => inline switch
        {
            Inline.Text text => text.Value,
            Inline.Code code => code.Value,
            Inline.WikiLink wiki => wiki.Alias is null ? wiki.Target : wiki.Alias,
            Inline.Link link => Rendered(link.Label),
            Inline.Emphasis emphasis => Rendered(emphasis.Children),
            // An inline image inside a sentence is a marker, not a picture: the
            // paragraph is a wrapping TextBlock, and a real image element cannot live
            // inside one. The image on its own line is the case that gets drawn.
            Inline.Image image => $"[image: {image.Alt}]",
            _ => string.Empty,
        }));

    // --- Resizing the preview -------------------------------------------------

    private const double DefaultPreviewWidth = 380;
    private const double MinimumPreviewWidth = 240;

    private bool _resizingPreview;
    private double _resizeAnchorX;
    private double _resizeAnchorWidth;
    private Pointer? _resizePointer;

    /// <summary>
    /// Drag the divider to change how much room the preview gets.
    ///
    /// The width is recorded during the drag and *applied* on the next dispatcher
    /// turn. That is not fastidiousness: setting a column's width from inside a
    /// PointerMoved handler re-enters layout while the tree is mid-measure, and this
    /// runtime dies with a stowed native exception (0xc000027b) when it happens. The
    /// same fix as the diagram's relayout, for the same reason - and both of them
    /// are in this file because both were found by the same crash.
    /// </summary>
    private void OnPreviewSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        _resizingPreview = true;
        _resizeAnchorX = point.Position.X;
        _resizeAnchorWidth = PreviewHost.Width;

        if (e.Pointer is not null)
        {
            _resizePointer = e.Pointer;
            PreviewSplitter.CapturePointer(e.Pointer);
        }
    }

    private void OnPreviewSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingPreview) return;

        var x = e.GetCurrentPoint(this).Position.X;
        var wanted = _resizeAnchorWidth - (x - _resizeAnchorX);

        if (wanted < MinimumPreviewWidth) wanted = MinimumPreviewWidth;

        // Never wider than the window, or the editor is pushed off the left edge and
        // the note you are editing becomes unreachable.
        var ceiling = Math.Max(MinimumPreviewWidth, MainContent.ActualWidth - MinimumEditorWidth);
        if (wanted > ceiling) wanted = ceiling;

        var width = wanted;
        DispatcherQueue.TryEnqueue(() => PreviewHost.Width = width);
    }

    private void OnPreviewSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizingPreview = false;

        if (_resizePointer is not null)
        {
            PreviewSplitter.ReleasePointerCapture(_resizePointer);
            _resizePointer = null;
        }

        // Persisted on release rather than during the drag, so a hundred deferred
        // writes are not made and abandoned.
        AppSession.PreviewWidth = PreviewHost.Width;
    }

    private const double MinimumEditorWidth = 320;

    private void OnPreviewSplitterDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ApplyPreviewWidth(AppSession.PreviewWidth ?? DefaultPreviewWidth);
        StatusText.Text = "The preview is back to its usual width.";
    }

    private void ApplyPreviewWidth(double width)
    {
        PreviewHost.Width = Math.Max(width, MinimumPreviewWidth);
    }

    /// <summary>
    /// Turns the preview on for a note that has a diagram in it.
    ///
    /// This is the fix for "it still just shows the raw markdown". It did, and the
    /// reason was not a bug: the middle of the window is the *source* and never
    /// stops being the source (ADR 0013), and the preview is off by default and
    /// lives behind Ctrl+I. A note whose whole point is a picture, opened with the
    /// preview off, is a wall of fences - and there is nothing in the window that
    /// says the picture is there.
    ///
    /// So the presence of a diagram is enough to turn it on. Not the other way
    /// round: a note with no diagram never has the preview forced open, because that
    /// would be rearranging the window behind the person's back every time they
    /// opened anything. And Ctrl+I still turns it off, because a toggle that
    /// cannot be turned off is not a toggle.
    /// </summary>
    private void ShowPreviewIfTheNoteHasDiagrams(string markdown)
    {
        if (PreviewHost.Visibility == Visibility.Visible) return;
        if (!markdown.Contains("```mermaid", StringComparison.OrdinalIgnoreCase)) return;

        TogglePreview();
        StatusText.Text = "This note has a diagram, so the preview is on. Ctrl+I turns it off.";
    }
}
