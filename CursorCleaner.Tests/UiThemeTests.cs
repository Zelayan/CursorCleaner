using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CursorCleaner.ViewModels;

namespace CursorCleaner.Tests;

[TestClass]
public class UiThemeTests
{
    [TestMethod]
    public void StatusAndKeyedTextStylesFollowRuntimeThemeChanges()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();

                ReplaceTheme(app, "Light.xaml");
                var status = CreateTextBlock(app, "StatusText");
                status.Tag = StatusSeverity.Warning;
                var pageTitle = CreateTextBlock(app, "PageTitle");
                var sectionHeader = CreateTextBlock(app, "SectionHeader");

                var statValue = CreateTextBlock(app, "StatValue");
                var statLabel = CreateTextBlock(app, "StatLabel");
                var panel = new StackPanel();
                panel.Children.Add(status);
                panel.Children.Add(pageTitle);
                panel.Children.Add(sectionHeader);
                panel.Children.Add(statValue);
                panel.Children.Add(statLabel);
                var window = new Window { Content = panel, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Width = 1, Height = 1, Left = -10000, Top = -10000 };
                app.MainWindow = window;
                window.Show();

                var lightStatusBrush = AssertBrush(status.Foreground, Color.FromRgb(0x8A, 0x4B, 0x00));
                AssertBrush(pageTitle.Foreground, Color.FromRgb(0x1A, 0x1A, 0x1A));
                AssertBrush(sectionHeader.Foreground, Color.FromRgb(0x1A, 0x1A, 0x1A));
                AssertBrush(statValue.Foreground, Color.FromRgb(0x1A, 0x1A, 0x1A));
                AssertBrush(statLabel.Foreground, Color.FromRgb(0x56, 0x56, 0x56));

                ReplaceTheme(app, "Dark.xaml");

                var darkStatusBrush = AssertBrush(status.Foreground, Color.FromRgb(0xFF, 0xCC, 0x4D));
                Assert.AreNotSame(lightStatusBrush, darkStatusBrush, "Status foreground retained the brush from the previous theme.");
                AssertBrush(pageTitle.Foreground, Color.FromRgb(0xF2, 0xF2, 0xF2));
                AssertBrush(sectionHeader.Foreground, Color.FromRgb(0xF2, 0xF2, 0xF2));
                AssertBrush(statValue.Foreground, Color.FromRgb(0xF2, 0xF2, 0xF2));
                AssertBrush(statLabel.Foreground, Color.FromRgb(0xC4, 0xC4, 0xC4));

                window.Close();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) Assert.Fail(failure.ToString());
    }

    private static void ReplaceTheme(Application app, string fileName)
    {
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"/CursorCleaner;component/Resources/{fileName}", UriKind.Relative)
        };
        app.Resources.MergedDictionaries[0] = replacement;
    }

    private static TextBlock CreateTextBlock(Application resourceOwner, string styleKey) =>
        new() { Style = (Style)resourceOwner.FindResource(styleKey) };

    private static SolidColorBrush AssertBrush(Brush brush, Color expected)
    {
        var solid = brush as SolidColorBrush;
        Assert.IsNotNull(solid);
        Assert.AreEqual(expected, solid.Color);
        return solid;
    }
}
