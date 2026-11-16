using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Nexode.App.ViewModels;

namespace Nexode.App.Services;

/// <summary>
/// UI animations (tab change, menus, dialogs, side panels, hover) with a global switch and speed factor from the
/// settings. Live sessions are never animated (the RDP window cannot be faded, terminals should not lag).
/// </summary>
public static class Motion
{
    private static Styles? _styles;

    public static bool Enabled => AppHost.Settings.Animations && AppHost.Settings.AnimationSpeed > 0;

    /// <summary>Duration for a base value in milliseconds at speed 1.0 (zero while animations are off).</summary>
    public static TimeSpan Duration(double ms) => Enabled ? TimeSpan.FromMilliseconds(ms / Math.Clamp(AppHost.Settings.AnimationSpeed, 0.1, 4)) : TimeSpan.Zero;

    public static readonly double[] Speeds = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>(Re)applies the animation styles (menus, flyouts, hover fades) after a settings change.</summary>
    public static void Apply(Application app)
    {
        if (_styles is not null) app.Styles.Remove(_styles);
        _styles = new Styles();
        if (Enabled)
        {
            var menu = Duration(130);
            foreach (var type in new[] { typeof(MenuFlyoutPresenter), typeof(ContextMenu), typeof(FlyoutPresenter) })
            {
                var style = new Style(x => x.OfType(type));
                style.Animations.Add(FadeIn(menu, 4));
                _styles.Add(style);
            }

            var hover = new Style(x => x.OfType<Button>().Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"));
            hover.Setters.Add(new Setter(Animatable.TransitionsProperty, new Transitions
            {
                new BrushTransition { Property = ContentPresenter.BackgroundProperty, Duration = Duration(110) }
            }));
            _styles.Add(hover);
            var border = new Style(x => x.OfType<Border>().Class("tilecard"));
            border.Setters.Add(new Setter(Animatable.TransitionsProperty, new Transitions
            {
                new BrushTransition { Property = Border.BackgroundProperty, Duration = Duration(120) },
                new BrushTransition { Property = Border.BorderBrushProperty, Duration = Duration(120) }
            }));
            _styles.Add(border);

            if (AppHost.Settings.TreeAnimation)
            {
                // Tree: rows of an expanded folder slide in, rows of a collapsing folder fade out.
                var appear = new Style(x => x.OfType<Border>().Class("row").Class("appear"));
                appear.Animations.Add(FadeIn(Duration(170), 6));
                _styles.Add(appear);
                var leave = new Style(x => x.OfType<Border>().Class("row").Class("leaving"));
                var fade = new Animation { Duration = Duration(120), Easing = new CubicEaseIn(), FillMode = FillMode.Forward };
                fade.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0), new Setter(TranslateTransform.YProperty, 0.0) } });
                fade.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0.0), new Setter(TranslateTransform.YProperty, -4.0) } });
                leave.Animations.Add(fade);
                _styles.Add(leave);
            }
        }

        app.Styles.Add(_styles);
    }

    private static Animation FadeIn(TimeSpan duration, double offsetY)
    {
        var a = new Animation { Duration = duration, Easing = new CubicEaseOut(), FillMode = FillMode.Forward };
        a.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0.0), new Setter(TranslateTransform.YProperty, -offsetY) } });
        a.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0), new Setter(TranslateTransform.YProperty, 0.0) } });
        return a;
    }

    /// <summary>Fades and slightly lifts a freshly opened window's content.</summary>
    public static void AnimateWindow(Window window)
    {
        if (!Enabled || window.Content is not Visual content) return;
        _ = Run(content, Duration(170), 0.0, 10, 0.98);
    }

    /// <summary>Opacity 0→1, offset → 0, scale → 1 on <paramref name="target"/>.</summary>
    public static async Task Run(Visual target, TimeSpan duration, double fromOpacity, double fromY, double fromScale = 1, CancellationToken ct = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            target.Opacity = 1;
            return;
        }

        var translate = new TranslateTransform(0, fromY);
        var scale = new ScaleTransform(fromScale, fromScale);
        target.RenderTransformOrigin = RelativePoint.Center;
        target.RenderTransform = new TransformGroup { Children = { scale, translate } };
        target.Opacity = fromOpacity;
        var easing = new CubicEaseOut();
        var fade = new Animation { Duration = duration, Easing = easing, FillMode = FillMode.Forward };
        fade.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, fromOpacity) } });
        fade.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0) } });
        var move = new Animation { Duration = duration, Easing = easing, FillMode = FillMode.Forward };
        move.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, fromY) } });
        move.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, 0.0) } });
        var grow = new Animation { Duration = duration, Easing = easing, FillMode = FillMode.Forward };
        grow.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, fromScale), new Setter(ScaleTransform.ScaleYProperty, fromScale) } });
        grow.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) } });
        try
        {
            await Task.WhenAll(fade.RunAsync(target, ct), move.RunAsync(translate, ct), grow.RunAsync(scale, ct));
        }
        finally
        {
            target.Opacity = 1;
            target.RenderTransform = null;
        }
    }
}

/// <summary>Tab change: the new page fades in and settles; sessions switch instantly.</summary>
public sealed class TabTransition : IPageTransition
{
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (from is not null) from.IsVisible = false;
        if (to is null) return;
        to.IsVisible = true;
        var isSession = to is ContentPresenter { Content: TabViewModel { IsSession: true } } || from is ContentPresenter { Content: TabViewModel { IsSession: true } };
        if (cancellationToken.IsCancellationRequested || isSession || !Motion.Enabled)
        {
            to.Opacity = 1;
            to.RenderTransform = null;
            return;
        }

        await Motion.Run(to, Motion.Duration(190), 0.0, 8, 1, cancellationToken);
    }
}
