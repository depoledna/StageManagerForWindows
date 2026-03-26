using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using StageManager.Model;

namespace StageManager.Animations
{
	internal class SceneTransitionAnimator
	{
		private const int AnimationDurationMs = 300;

		private TransitionOverlayWindow _overlay;
		private bool _isAnimating;

		public bool IsAnimating => _isAnimating;

		/// <summary>
		/// Animates placeholders for both the incoming and outgoing scenes simultaneously.
		/// Incoming: sidebarSlot → incomingTarget (small→big, left→right)
		/// Outgoing: outgoingSource → sidebarSlot (big→small, right→left)
		/// Pass Rect.Empty for outgoingSource to skip the outgoing animation.
		/// </summary>
		public Task AnimateSceneTransitionAsync(
			Rect incomingSource, Rect incomingTarget, SceneModel incomingScene,
			Rect outgoingSource, Rect outgoingTarget, SceneModel outgoingScene)
		{
			if (_isAnimating)
			{
				Log.Info("ANIM", "Skipped — animation already in progress");
				return Task.CompletedTask;
			}

			_isAnimating = true;

			var tcs = new TaskCompletionSource<bool>();

			try
			{
				// Overlay must cover all animation bounds
				var overlayBounds = Rect.Union(incomingSource, incomingTarget);
				if (outgoingSource != Rect.Empty)
				{
					overlayBounds = Rect.Union(overlayBounds, outgoingSource);
					overlayBounds = Rect.Union(overlayBounds, outgoingTarget);
				}
				EnsureOverlay(overlayBounds);

				var overlayLeft = _overlay.Left;
				var overlayTop = _overlay.Top;

				var duration = new Duration(TimeSpan.FromMilliseconds(AnimationDurationMs));
				var easing = new PowerEase { EasingMode = EasingMode.EaseOut };

				var storyboard = new Storyboard();

				// --- Incoming placeholder (sidebar → window position) ---
				var inPlaceholder = CreatePlaceholder(incomingScene);
				var inFromLeft = incomingSource.X - overlayLeft;
				var inFromTop = incomingSource.Y - overlayTop;
				var inToLeft = incomingTarget.X - overlayLeft;
				var inToTop = incomingTarget.Y - overlayTop;

				Canvas.SetLeft(inPlaceholder, inFromLeft);
				Canvas.SetTop(inPlaceholder, inFromTop);
				inPlaceholder.Width = incomingSource.Width;
				inPlaceholder.Height = incomingSource.Height;
				_overlay.Canvas.Children.Add(inPlaceholder);

				AddAnimations(storyboard, inPlaceholder, duration, easing,
					inFromLeft, inToLeft, inFromTop, inToTop,
					incomingSource.Width, incomingTarget.Width,
					incomingSource.Height, incomingTarget.Height);

				Log.Info("ANIM", $"Incoming: ({inFromLeft:F0},{inFromTop:F0} {incomingSource.Width:F0}x{incomingSource.Height:F0}) → ({inToLeft:F0},{inToTop:F0} {incomingTarget.Width:F0}x{incomingTarget.Height:F0})");

				// --- Outgoing placeholder (window position → sidebar) ---
				if (outgoingSource != Rect.Empty && outgoingScene != null)
				{
					var outPlaceholder = CreatePlaceholder(outgoingScene);
					var outFromLeft = outgoingSource.X - overlayLeft;
					var outFromTop = outgoingSource.Y - overlayTop;
					var outToLeft = outgoingTarget.X - overlayLeft;
					var outToTop = outgoingTarget.Y - overlayTop;

					Canvas.SetLeft(outPlaceholder, outFromLeft);
					Canvas.SetTop(outPlaceholder, outFromTop);
					outPlaceholder.Width = outgoingSource.Width;
					outPlaceholder.Height = outgoingSource.Height;
					_overlay.Canvas.Children.Add(outPlaceholder);

					AddAnimations(storyboard, outPlaceholder, duration, easing,
						outFromLeft, outToLeft, outFromTop, outToTop,
						outgoingSource.Width, outgoingTarget.Width,
						outgoingSource.Height, outgoingTarget.Height);

					Log.Info("ANIM", $"Outgoing: ({outFromLeft:F0},{outFromTop:F0} {outgoingSource.Width:F0}x{outgoingSource.Height:F0}) → ({outToLeft:F0},{outToTop:F0} {outgoingTarget.Width:F0}x{outgoingTarget.Height:F0})");
				}

				Log.Info("ANIM", $"Overlay: {_overlay.Left:F0},{_overlay.Top:F0} {_overlay.Width:F0}x{_overlay.Height:F0}, placeholders={_overlay.Canvas.Children.Count}");
				_overlay.Show();

				storyboard.Completed += (s, e) =>
				{
					Log.Info("ANIM", "Storyboard completed, removing placeholders");
					_overlay.Canvas.Children.Clear();
					_overlay.Hide();
					_isAnimating = false;
					tcs.TrySetResult(true);
				};

				storyboard.Begin();
			}
			catch (Exception ex)
			{
				Log.Info("ANIM", $"Transition failed: {ex.Message}");
				_isAnimating = false;
				_overlay?.Hide();
				tcs.TrySetResult(false);
			}

			return tcs.Task;
		}

		private void EnsureOverlay(Rect bounds)
		{
			if (_overlay == null)
			{
				_overlay = new TransitionOverlayWindow();
			}

			_overlay.Left = bounds.X;
			_overlay.Top = bounds.Y;
			_overlay.Width = bounds.Width;
			_overlay.Height = bounds.Height;
		}

		private static void AddAnimations(Storyboard storyboard, UIElement target,
			Duration duration, IEasingFunction easing,
			double fromLeft, double toLeft, double fromTop, double toTop,
			double fromWidth, double toWidth, double fromHeight, double toHeight)
		{
			storyboard.Children.Add(MakeAnimation(fromLeft, toLeft, duration, easing, Canvas.LeftProperty, target));
			storyboard.Children.Add(MakeAnimation(fromTop, toTop, duration, easing, Canvas.TopProperty, target));
			storyboard.Children.Add(MakeAnimation(fromWidth, toWidth, duration, easing, FrameworkElement.WidthProperty, target));
			storyboard.Children.Add(MakeAnimation(fromHeight, toHeight, duration, easing, FrameworkElement.HeightProperty, target));
		}

		private static Border CreatePlaceholder(SceneModel sceneModel)
		{
			var icon = sceneModel?.Windows.FirstOrDefault()?.Icon;

			var iconImage = new System.Windows.Controls.Image
			{
				Source = icon,
				Width = 48,
				Height = 48,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};

			return new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(220, 240, 240, 240)),
				CornerRadius = new CornerRadius(8),
				Child = iconImage,
				Effect = new DropShadowEffect
				{
					BlurRadius = 30,
					ShadowDepth = 4,
					Opacity = 0.4
				},
				ClipToBounds = false
			};
		}

		private static DoubleAnimation MakeAnimation(double from, double to, Duration duration, IEasingFunction easing, DependencyProperty property, UIElement target)
		{
			var anim = new DoubleAnimation(from, to, duration)
			{
				EasingFunction = easing
			};
			Storyboard.SetTarget(anim, target);
			Storyboard.SetTargetProperty(anim, new PropertyPath(property));
			return anim;
		}

		public void Dispose()
		{
			if (_overlay != null)
			{
				_overlay.Close();
				_overlay = null;
			}
		}
	}
}
