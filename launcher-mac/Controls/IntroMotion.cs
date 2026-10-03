using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Text.RegularExpressions;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>
/// Prime opening sequence (about two seconds, skippable). Every move says something: the mark
/// arrives, the name assembles, Prime is stamped, and the curtain lifts onto a cascading interface.
/// </summary>
internal static class IntroMotion
{
    private static CancellationTokenSource? _skip;

    public static void Skip() => _skip?.Cancel();

    /// <summary>Plays the intro; whatever happens, the interface ends up visible.</summary>
    public static async Task PlayAsync(MainWindow window)
    {
        try { await PlayCoreAsync(window); }
        catch (Exception error)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dustore-intro-error.txt"), error.ToString()); } catch (Exception) { }
        }
        finally
        {
            if (window.FindControl<Grid>("IntroLayer") is { } layer) layer.IsVisible = false;
            foreach (string name in new[] { "Sidebar", "MainColumn" })
                if (window.FindControl<Control>(name) is { } part) { part.Opacity = 1; part.RenderTransform = null; }
        }
    }

    private static async Task PlayCoreAsync(MainWindow window)
    {
        var layer = window.FindControl<Grid>("IntroLayer");
        var logo = window.FindControl<Border>("IntroLogo");
        var word = window.FindControl<StackPanel>("IntroWord");
        var badge = window.FindControl<Border>("IntroBadge");
        var sidebar = window.FindControl<Border>("Sidebar");
        var main = window.FindControl<Grid>("MainColumn");
        if (layer is null || logo is null || word is null || badge is null || sidebar is null || main is null) return;
        _skip = new CancellationTokenSource();
        var token = _skip.Token;
        window.KeyDown += (_, _) => Skip();

        // Build the wordmark letter by letter so each can rise on its own beat.
        var accent = window.FindResource("Accent") as IBrush;
        var letters = new List<TextBlock>();
        foreach (char ch in "Dustore V")
        {
            var letter = new TextBlock
            {
                Text = ch.ToString(), FontSize = 40, FontWeight = FontWeight.Bold, Opacity = 0, LetterSpacing = -0.5,
                FontFamily = window.FindResource("DisplayFont") as FontFamily ?? FontFamily.Default,
                RenderTransform = TransformOperations.Parse("translateY(18px)")
            };
            if (ch == 'V' && accent is not null) letter.Foreground = accent;
            letters.Add(letter);
            word.Children.Add(letter);
        }
        sidebar.Opacity = 0; main.Opacity = 0;
        layer.IsVisible = true;

        try
        {
            var mark = Animate(logo, 520, new BackEaseOut(),
                (0, 0d, "scale(0.6) rotate(-8deg)"), (1, 1d, "scale(1) rotate(0deg)"));
            await Delay(300, token);
            var rising = letters.Select((letter, i) => DelayThen(i * 35, () =>
                Animate(letter, 420, new CubicEaseOut(), (0, 0d, "translateY(18px)"), (1, 1d, "translateY(0px)")), token)).ToList();
            await Delay(500, token);
            var stamp = Animate(badge, 360, new BackEaseOut(), (0, 0d, "scale(0.7)"), (1, 1d, "scale(1)"));
            await Task.WhenAll(rising.Append(mark).Append(stamp));
            await Delay(380, token);
        }
        catch (OperationCanceledException) { }

        // Curtain lifts; the interface arrives behind it in a short cascade.
        var lift = Animate(layer, 520, new CubicEaseInOut(), (0, 1d, "translateY(0px)"), (1, 0d, "translateY(-40px)"));
        var side = Animate(sidebar, 560, new CubicEaseOut(), (0, 0d, "translateX(-28px)"), (1, 1d, "translateX(0px)"));
        var content = DelayThen(90, () => Animate(main, 620, new CubicEaseOut(), (0, 0d, "translateY(28px)"), (1, 1d, "translateY(0px)")), CancellationToken.None);
        await Task.WhenAll(lift, side, content);
        layer.IsVisible = false;
        sidebar.Opacity = 1; main.Opacity = 1;
        sidebar.RenderTransform = null; main.RenderTransform = null;
    }

    // A small frame tween over explicit transforms: opacity, translate, scale and rotation.
    private static Task Animate(Visual target, int milliseconds, Easing easing, params (double Cue, double Opacity, string Transform)[] frames)
    {
        var from = Parse(frames[0].Transform); var to = Parse(frames[^1].Transform);
        double o0 = frames[0].Opacity, o1 = frames[^1].Opacity;
        var scale = new ScaleTransform(); var rotate = new RotateTransform(); var translate = new TranslateTransform();
        target.RenderTransform = new TransformGroup { Children = { scale, rotate, translate } };
        var done = new TaskCompletionSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Step(double t)
        {
            double k = easing.Ease(Math.Clamp(t, 0, 1));
            target.Opacity = o0 + (o1 - o0) * Math.Clamp(k, 0, 1);
            scale.ScaleX = scale.ScaleY = from.Scale + (to.Scale - from.Scale) * k;
            rotate.Angle = from.Rotate + (to.Rotate - from.Rotate) * k;
            translate.X = from.X + (to.X - from.X) * k;
            translate.Y = from.Y + (to.Y - from.Y) * k;
        }
        Step(0);
        DispatcherTimer.Run(() =>
        {
            double t = clock.Elapsed.TotalMilliseconds / milliseconds;
            Step(t);
            if (t < 1) return true;
            done.TrySetResult();
            return false;
        }, TimeSpan.FromMilliseconds(1000.0 / 60), DispatcherPriority.Render);
        return done.Task;
    }

    private static (double X, double Y, double Scale, double Rotate) Parse(string transform)
    {
        double x = 0, y = 0, sc = 1, rot = 0;
        foreach (Match m in Regex.Matches(transform, @"(\w+)\(([-\d.]+)(?:px|deg)?\)"))
        {
            double v = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            switch (m.Groups[1].Value) { case "translateX": x = v; break; case "translateY": y = v; break; case "scale": sc = v; break; case "rotate": rot = v; break; }
        }
        return (x, y, sc, rot);
    }

    private static async Task DelayThen(int milliseconds, Func<Task> next, CancellationToken token)
    {
        try { await Delay(milliseconds, token); } catch (OperationCanceledException) { }
        await next();
    }

    private static Task Delay(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token);
}
