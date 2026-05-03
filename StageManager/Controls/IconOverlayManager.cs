using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using StageManager.Animations;
using StageManager.Helpers;
using StageManager.Model;

namespace StageManager.Controls
{
    internal sealed class IconOverlayManager : IDisposable
    {
        private const double IconSize = 30;
        private const double IconGap = 4;
        private const double OverlapOffset = -14;
        private const double SmallSceneLeftShift = -8;
        private const double BottomOverlap = 15;
        private const double SceneBottomMargin = 28;
        private const int CompactThreshold = 2;

        // Filter-view layout (active when HighlightedProcessKey != null).
        private const double FilteredIconSize = 22;
        private const double FilteredBottomOverlap = 11;
        private const double LabelFontSize = 10;
        private const double LabelTopMargin = 2;
        private const double FilteredScale = FilteredIconSize / IconSize; // ≈ 0.733

        // Morph animation tuning. Shared by both icons and labels. Asymmetric easing matches the
        // sidebar: arriving/morphing icons decelerate (EaseOut), removed icons accelerate away (EaseIn).
        private static readonly Duration MorphDuration = new Duration(TimeSpan.FromMilliseconds(250));
        private static readonly Duration HoverDuration = new Duration(TimeSpan.FromMilliseconds(150));
        private static readonly IEasingFunction MorphEaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        private static readonly IEasingFunction MorphEaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        private static readonly DropShadowEffect SharedShadow = CreateFrozenShadow();

        private IconOverlayWindow? _overlay;

        // Live-element tracking lets us re-target existing Images/TextBlocks instead of rebuilding.
        // Stable identity = (sceneId, windowIndex) per icon, sceneId per label.
        private readonly Dictionary<(Guid sceneId, int idx), LiveIcon> _icons = new();
        private readonly Dictionary<Guid, LiveLabel> _labels = new();

        public bool Enabled { get; set; } = true;

        public Action<string>? OnIconClicked { get; set; }

        // Setter side effect: while filter is active, freeze the icon strip — no hover, no click,
        // no cursor change. IsHitTestVisible=false silences MouseEnter/Leave/LeftButtonUp and
        // Cursors.Hand all at once. Also defeats the per-frame WS_EX_TRANSPARENT toggle in
        // IconOverlayWindow.OnRenderTick (VisualTreeHelper.HitTest skips non-hit-testable elements),
        // so clicks on the icon area fall through the overlay.
        private string? _highlightedProcessKey;
        public string? HighlightedProcessKey
        {
            get => _highlightedProcessKey;
            set
            {
                if (_highlightedProcessKey == value) return;
                _highlightedProcessKey = value;
                var hitTestable = value == null;
                foreach (var live in _icons.Values)
                    live.Image.IsHitTestVisible = hitTestable;
            }
        }

        private sealed class LiveIcon
        {
            public Image Image = null!;
            public ScaleTransform MorphScale = null!;  // tracks the layout scale (1.0 classic, 22/30 filtered)
            public ScaleTransform HoverScale = null!;  // hover-driven 1↔1.15, composed via TransformGroup
            public bool Removing;
        }

        private sealed class LiveLabel
        {
            public TextBlock Text = null!;
            public bool Removing;
        }

        private struct LayoutParams
        {
            public double VisualSize;
            public double VisualStride;
            public double FirstVisualCenterX;
            public double VisualCenterY;
            public double Scale;
        }

        public void UpdateIcons(
            IReadOnlyList<SceneModel> visibleScenes,
            Func<SceneModel, Rect> getSceneBounds,
            Rect workArea,
            double xOffset = 0)
        {
            if (!Enabled || workArea == Rect.Empty)
                return;

            EnsureOverlay(workArea);

            var filtered = HighlightedProcessKey != null;
            var nextIconKeys = new HashSet<(Guid, int)>();
            var nextLabelKeys = new HashSet<Guid>();
            int rendered = 0, totalIcons = 0;

            foreach (var scene in visibleScenes)
            {
                var bounds = getSceneBounds(scene);
                if (bounds == Rect.Empty || scene.Windows.Count == 0)
                    continue;

                var p = ComputeLayoutParams(scene.Windows.Count, bounds, filtered);

                for (int i = 0; i < scene.Windows.Count; i++)
                {
                    var window = scene.Windows[i];
                    if (window.Icon == null)
                        continue;

                    var processKey = window.Window?.ProcessFileName ?? string.Empty;
                    var dim = filtered && HighlightedProcessKey != processKey;
                    var visualCenterX = p.FirstVisualCenterX + i * p.VisualStride + xOffset;

                    nextIconKeys.Add((scene.Id, i));
                    ApplyIconTarget(scene, i, window, processKey,
                        visualCenterX, p.VisualCenterY, p.Scale, dim ? 0.45 : 1.0);
                    totalIcons++;
                }

                if (filtered)
                {
                    nextLabelKeys.Add(scene.Id);
                    var labelTopY = p.VisualCenterY + p.VisualSize / 2 + LabelTopMargin;
                    ApplyLabelTarget(scene, bounds.X + xOffset, labelTopY, bounds.Width);
                }

                rendered++;
            }

            // Anything left in the dictionaries that didn't get re-targeted this pass is leaving.
            foreach (var key in _icons.Keys.Where(k => !nextIconKeys.Contains(k)).ToArray())
                BeginRemoveIcon(key);
            foreach (var key in _labels.Keys.Where(k => !nextLabelKeys.Contains(k)).ToArray())
                BeginRemoveLabel(key);

            Log.Info("FILTER", $"UpdateIcons: visibleScenes={visibleScenes.Count} rendered={rendered} totalIcons={totalIcons} filtered={filtered} highlight='{HighlightedProcessKey ?? "<none>"}'");

            if (_overlay!.Canvas.Children.Count == 0)
                _overlay.Hide();
            else
                _overlay.Show();
        }

        // Layout math (visual = post-scale): icons are spaced by `stride` between visual centers.
        // Compact (count<=2): row uses fixed IconGap. Overlap (count>2): icons overlap by OverlapOffset.
        // For classic: row hugs the scene's left edge (with SmallSceneLeftShift for compact).
        // For filtered: row is centered horizontally under the thumbnail.
        private static LayoutParams ComputeLayoutParams(int count, Rect bounds, bool filtered)
        {
            var visualSize = filtered ? FilteredIconSize : IconSize;
            var scale = filtered ? FilteredScale : 1.0;
            var stride = (count <= CompactThreshold) ? (visualSize + IconGap) : (visualSize + OverlapOffset);
            var rowWidth = visualSize + (count - 1) * stride;
            var bottomOverlap = filtered ? FilteredBottomOverlap : BottomOverlap;
            var visualCenterY = bounds.Y + bounds.Height - SceneBottomMargin - bottomOverlap + visualSize / 2;

            double firstCenterX;
            if (filtered)
            {
                firstCenterX = bounds.X + bounds.Width / 2 - rowWidth / 2 + visualSize / 2;
            }
            else
            {
                var leftShift = (count <= CompactThreshold) ? SmallSceneLeftShift : 0;
                firstCenterX = bounds.X + leftShift + visualSize / 2;
            }

            return new LayoutParams
            {
                VisualSize = visualSize,
                VisualStride = stride,
                FirstVisualCenterX = firstCenterX,
                VisualCenterY = visualCenterY,
                Scale = scale,
            };
        }

        private void ApplyIconTarget(SceneModel scene, int idx, WindowModel window, string processKey,
            double visualCenterX, double visualCenterY, double scale, double opacity)
        {
            // Image's natural box is IconSize×IconSize; the ScaleTransform around its center shrinks
            // the rendered visual without moving the natural top-left, so a single Canvas.Left/Top
            // expression works for both states.
            var canvasPoint = new Point(visualCenterX - IconSize / 2, visualCenterY - IconSize / 2).ToCanvas(_overlay!);
            var canvasLeft = canvasPoint.X;
            var canvasTop = canvasPoint.Y;

            var key = (scene.Id, idx);
            if (_icons.TryGetValue(key, out var live))
            {
                // Re-claim an icon that was mid-fadeout: cancel the removal, animate to new target.
                live.Removing = false;
                AnimateDouble(live.Image, Canvas.LeftProperty, canvasLeft);
                AnimateDouble(live.Image, Canvas.TopProperty, canvasTop);
                AnimateScale(live.MorphScale, scale, MorphDuration);
                AnimateDouble(live.Image, UIElement.OpacityProperty, opacity);
            }
            else
            {
                var newLive = CreateLiveIcon(window.Icon, processKey, scene, idx, scale);
                _icons[key] = newLive;
                Canvas.SetLeft(newLive.Image, canvasLeft);
                Canvas.SetTop(newLive.Image, canvasTop);
                _overlay!.Canvas.Children.Add(newLive.Image);
                AnimateDouble(newLive.Image, UIElement.OpacityProperty, opacity);
            }
        }

        private LiveIcon CreateLiveIcon(ImageSource source, string processKey, SceneModel scene, int idx, double initialScale)
        {
            // Two scale transforms composed via TransformGroup: morph (layout) and hover (interaction).
            // They animate independently on the same Image without stomping each other.
            var morph = new ScaleTransform(initialScale, initialScale);
            var hover = new ScaleTransform(1, 1);
            var group = new TransformGroup();
            group.Children.Add(morph);
            group.Children.Add(hover);

            var image = new Image
            {
                Width = IconSize,
                Height = IconSize,
                Source = source,
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = group,
                Effect = SharedShadow,
                Opacity = 0,  // always start invisible — first ApplyIconTarget call animates to target opacity
            };
            image.IsHitTestVisible = HighlightedProcessKey == null;

            var sceneTitle = scene.Title;
            var windowTitle = scene.Windows[idx].Title;
            image.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Log.Info("FILTER", $"icon clicked: scene='{sceneTitle}' processKey='{processKey}' iconIndex={idx} window='{windowTitle}'");
                OnIconClicked?.Invoke(processKey);
            };
            image.MouseEnter += (_, _) => AnimateScale(hover, 1.15, HoverDuration);
            image.MouseLeave += (_, _) => AnimateScale(hover, 1.0, HoverDuration);

            return new LiveIcon { Image = image, MorphScale = morph, HoverScale = hover };
        }

        private void ApplyLabelTarget(SceneModel scene, double left, double top, double width)
        {
            var canvasPoint = new Point(left, top).ToCanvas(_overlay!);
            var canvasLeft = canvasPoint.X;
            var canvasTop = canvasPoint.Y;
            var text = BuildLabelText(scene, HighlightedProcessKey ?? "");

            if (_labels.TryGetValue(scene.Id, out var live))
            {
                live.Removing = false;
                live.Text.Text = text;
                live.Text.Width = width;
                AnimateDouble(live.Text, Canvas.LeftProperty, canvasLeft);
                AnimateDouble(live.Text, Canvas.TopProperty, canvasTop);
                AnimateDouble(live.Text, UIElement.OpacityProperty, 1.0);
            }
            else
            {
                var tb = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = LabelFontSize,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Width = width,
                    Effect = SharedShadow,
                    Opacity = 0,
                };
                Canvas.SetLeft(tb, canvasLeft);
                Canvas.SetTop(tb, canvasTop);
                _labels[scene.Id] = new LiveLabel { Text = tb };
                _overlay.Canvas.Children.Add(tb);
                AnimateDouble(tb, UIElement.OpacityProperty, 1.0);
            }
        }

        private void BeginRemoveIcon((Guid, int) key)
        {
            if (!_icons.TryGetValue(key, out var live)) return;
            if (live.Removing) return;
            live.Removing = true;

            var anim = Anim.To(0, MorphDuration, MorphEaseIn);
            anim.Completed += (_, _) =>
            {
                if (!live.Removing) return; // re-claimed mid-fade
                _overlay?.Canvas.Children.Remove(live.Image);
                _icons.Remove(key);
            };
            live.Image.BeginAnimation(UIElement.OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private void BeginRemoveLabel(Guid key)
        {
            if (!_labels.TryGetValue(key, out var live)) return;
            if (live.Removing) return;
            live.Removing = true;

            var anim = Anim.To(0, MorphDuration, MorphEaseIn);
            anim.Completed += (_, _) =>
            {
                if (!live.Removing) return;
                _overlay?.Canvas.Children.Remove(live.Text);
                _labels.Remove(key);
            };
            live.Text.BeginAnimation(UIElement.OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateDouble(UIElement target, DependencyProperty prop, double to)
        {
            target.BeginAnimation(prop, Anim.To(to, MorphDuration, MorphEaseOut), HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateScale(ScaleTransform t, double to, Duration duration)
        {
            t.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(to, duration, MorphEaseOut), HandoffBehavior.SnapshotAndReplace);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(to, duration, MorphEaseOut), HandoffBehavior.SnapshotAndReplace);
        }

        public void Show(Rect workArea)
        {
            if (workArea == Rect.Empty)
                return;

            EnsureOverlay(workArea);
            _overlay!.Show();
        }

        public void Hide()
        {
            _overlay?.Hide();
        }

        public void SlideIn(double offsetX, TimeSpan duration, IEasingFunction easing, Action? onCompleted = null)
        {
            if (_overlay == null) return;
            var transform = new TranslateTransform(offsetX, 0);
            _overlay.Canvas.RenderTransform = transform;
            var anim = Anim.From(offsetX, 0, new Duration(duration), easing);
            anim.Completed += (_, _) =>
            {
                _overlay.Canvas.RenderTransform = Transform.Identity;
                onCompleted?.Invoke();
            };
            transform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        public void SlideOut(double offsetX, TimeSpan duration, IEasingFunction easing)
        {
            if (_overlay == null) return;
            var transform = _overlay.Canvas.RenderTransform as TranslateTransform ?? new TranslateTransform();
            _overlay.Canvas.RenderTransform = transform;
            var anim = Anim.From(0, offsetX, new Duration(duration), easing);
            anim.Completed += (_, _) => Hide();
            transform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        public void Dispose()
        {
            _icons.Clear();
            _labels.Clear();
            _overlay?.Close();
            _overlay = null;
        }

        private void EnsureOverlay(Rect bounds)
        {
            _overlay ??= new IconOverlayWindow();
            _overlay.PositionFrom(bounds);
        }

        private static string BuildLabelText(SceneModel scene, string filterFilename)
        {
            // WindowModel ctor throws on null IWindow, so w.Window is non-null here.
            var distinct = scene.Windows
                .GroupBy(w => w.Window.ProcessFileName)
                .Select(g => new
                {
                    Filename = g.Key,
                    Friendly = TitleCase(g.First().Window.ProcessName),
                    FirstTitle = g.First().Title,
                })
                .ToList();

            if (distinct.Count == 0)
                return string.Empty;

            if (distinct.Count == 1)
            {
                var only = distinct[0];
                return string.IsNullOrWhiteSpace(only.FirstTitle) ? only.Friendly : only.FirstTitle;
            }

            var matched = distinct.FirstOrDefault(d => d.Filename == filterFilename);
            var ordered = matched != null
                ? new[] { matched }.Concat(distinct.Where(d => d != matched))
                : distinct;
            return string.Join(", ", ordered.Select(d => d.Friendly));
        }

        private static string TitleCase(string name)
            => string.IsNullOrEmpty(name) ? string.Empty : char.ToUpper(name[0]) + name.Substring(1);

        private static DropShadowEffect CreateFrozenShadow()
        {
            var effect = new DropShadowEffect { BlurRadius = 20 };
            effect.Freeze();
            return effect;
        }
    }
}
