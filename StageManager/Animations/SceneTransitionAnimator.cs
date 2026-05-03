using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using StageManager.Helpers;
using StageManager.Model;

namespace StageManager.Animations
{
	internal class SceneTransitionAnimator : IDisposable
	{
		private const int AnimationDurationMs = 300;

		private TransitionOverlayWindow _overlay;
		private bool _isAnimating;

		public bool IsAnimating => _isAnimating;

		internal TransitionOverlayWindow Overlay => _overlay;

		internal TransitionOverlayWindow GetOrCreateOverlay(Rect bounds)
		{
			EnsureOverlay(bounds);
			return _overlay;
		}

		/// <summary>
		/// Pre-creates the overlay window so the first animation has no HWND-creation lag.
		/// </summary>
		public void WarmUp(Rect bounds)
		{
			EnsureOverlay(bounds);
			_overlay.Show();
			_overlay.Hide();
			Log.Info("ANIM", "Overlay warmed up");
		}

		/// <summary>
		/// Animates placeholders for both the incoming and outgoing scenes simultaneously.
		/// Pass Rect.Empty for outgoingSource to skip the outgoing animation.
		/// </summary>
		public Task AnimateSceneTransitionAsync(
			Rect overlayBounds,
			Rect incomingSource, Rect incomingTarget, SceneModel incomingScene,
			Rect outgoingSource, Rect outgoingTarget, SceneModel outgoingScene)
		{
			if (_isAnimating) return Task.CompletedTask;
			_isAnimating = true;
			var tcs = new TaskCompletionSource<bool>();

			try
			{
				EnsureOverlay(overlayBounds);

				var duration = new Duration(TimeSpan.FromMilliseconds(AnimationDurationMs));
				var easing = new PowerEase { EasingMode = EasingMode.EaseOut };
				var storyboard = new Storyboard();

				// Track placeholders so we can remove exactly these on completion
				Border inPlaceholder = null;
				Border outPlaceholder = null;

				// --- Incoming placeholder (sidebar → window position) ---
				var inIcon = incomingScene?.Windows.FirstOrDefault()?.Icon;
				inPlaceholder = PlaceholderFactory.Create(inIcon);
				var inFrom = incomingSource.ToCanvas(_overlay);
				var inTo = incomingTarget.ToCanvas(_overlay);
				SetupPlaceholder(inPlaceholder, storyboard, duration, easing, inFrom, inTo);
				_overlay.Canvas.Children.Add(inPlaceholder);

				Log.Info("ANIM", $"Incoming: ({inFrom.X:F0},{inFrom.Y:F0} {inFrom.Width:F0}x{inFrom.Height:F0}) → ({inTo.X:F0},{inTo.Y:F0} {inTo.Width:F0}x{inTo.Height:F0})");

				// --- Outgoing placeholder (window position → sidebar) ---
				if (outgoingSource != Rect.Empty && outgoingScene != null)
				{
					var outIcon = outgoingScene.Windows.FirstOrDefault()?.Icon;
					outPlaceholder = PlaceholderFactory.Create(outIcon);
					var outFrom = outgoingSource.ToCanvas(_overlay);
					var outTo = outgoingTarget.ToCanvas(_overlay);
					SetupPlaceholder(outPlaceholder, storyboard, duration, easing, outFrom, outTo);
					_overlay.Canvas.Children.Add(outPlaceholder);

					Log.Info("ANIM", $"Outgoing: ({outFrom.X:F0},{outFrom.Y:F0} {outFrom.Width:F0}x{outFrom.Height:F0}) → ({outTo.X:F0},{outTo.Y:F0} {outTo.Width:F0}x{outTo.Height:F0})");
				}

				Log.Info("ANIM", $"Overlay: {_overlay.Left:F0},{_overlay.Top:F0} {_overlay.Width:F0}x{_overlay.Height:F0}, placeholders={_overlay.Canvas.Children.Count}");
				_overlay.Show();

				storyboard.Completed += (s, e) =>
				{
					Log.Info("ANIM", "Storyboard completed, removing placeholders");
					if (inPlaceholder != null) _overlay.Canvas.Children.Remove(inPlaceholder);
					if (outPlaceholder != null) _overlay.Canvas.Children.Remove(outPlaceholder);
					if (_overlay.Canvas.Children.Count == 0) _overlay.Hide();
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
			_overlay ??= new TransitionOverlayWindow();
			_overlay.PositionFrom(bounds);
		}

		private static void SetupPlaceholder(Border placeholder, Storyboard storyboard,
			Duration duration, IEasingFunction easing, Rect from, Rect to)
		{
			Canvas.SetLeft(placeholder, from.X);
			Canvas.SetTop(placeholder, from.Y);
			placeholder.Width = from.Width;
			placeholder.Height = from.Height;

			storyboard.Children.Add(Anim.Storyboard(from.X, to.X, duration, easing, placeholder, Canvas.LeftProperty));
			storyboard.Children.Add(Anim.Storyboard(from.Y, to.Y, duration, easing, placeholder, Canvas.TopProperty));
			storyboard.Children.Add(Anim.Storyboard(from.Width, to.Width, duration, easing, placeholder, FrameworkElement.WidthProperty));
			storyboard.Children.Add(Anim.Storyboard(from.Height, to.Height, duration, easing, placeholder, FrameworkElement.HeightProperty));
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
