using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StageManager.Controls;
using StageManager.Helpers;
using StageManager.Model;

namespace StageManager.Animations
{
	internal class SceneTransitionAnimator : IDisposable
	{
		private const int AnimationDurationMs = 300;

		private TransitionOverlayWindow? _overlay;
		private bool _isAnimating;

		public bool IsAnimating => _isAnimating;

		internal TransitionOverlayWindow? Overlay => _overlay;

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
		/// Flies the incoming and outgoing scenes simultaneously as live cards: the
		/// incoming scene (clicked) travels sidebar → stage unskewing 31° → flat; the
		/// outgoing scene (current) travels stage → sidebar skewing flat → 31°. The
		/// incoming card borrows its tray tile's capture; the outgoing card — which
		/// has no tray tile while current — captures its window into an owned session.
		/// Either side falls back to a static icon card when no capture is available.
		/// Pass Rect.Empty for outgoingSource to skip the outgoing animation.
		/// </summary>
		public Task AnimateSceneTransitionAsync(
			Rect overlayBounds,
			Rect incomingSource, Rect incomingTarget, SceneModel incomingScene, CompositionThumbnail? incomingTile,
			Rect outgoingSource, Rect outgoingTarget, SceneModel? outgoingScene, IntPtr outgoingHandle,
			Point dpi, double cornerRadius)
		{
			if (_isAnimating) return Task.CompletedTask;
			_isAnimating = true;
			var tcs = new TaskCompletionSource<bool>();

			// Hoisted so the catch block can release cards already added to the overlay.
			IFlyingCard? incoming = null;
			IFlyingCard? outgoing = null;

			try
			{
				EnsureOverlay(overlayBounds);

				incoming = LiveCardHost.TryBorrow(_overlay, incomingTile, dpi, cornerRadius) as IFlyingCard
					?? new BorderCard(_overlay, incomingScene?.Windows.FirstOrDefault()?.Icon);
				incoming.Update(incomingSource, CompositionThumbnail.TrayTiltDegrees);
				Log.Info("ANIM", $"Incoming: {Fmt(incomingSource)} → {Fmt(incomingTarget)} (live={incoming is LiveCardHost})");

				bool hasOutgoing = outgoingSource != Rect.Empty && outgoingScene != null;
				if (hasOutgoing)
				{
					outgoing = LiveCardHost.TryCreateOwned(_overlay, outgoingHandle, dpi, cornerRadius) as IFlyingCard
						?? new BorderCard(_overlay, outgoingScene!.Windows.FirstOrDefault()?.Icon);
					outgoing.Update(outgoingSource, 0.0);
					Log.Info("ANIM", $"Outgoing: {Fmt(outgoingSource)} → {Fmt(outgoingTarget)} (live={outgoing is LiveCardHost})");
				}

				_overlay.Show();

				RunFlight(tcs,
					incoming, incomingSource, incomingTarget,
					outgoing, outgoingSource, outgoingTarget);
			}
			catch (Exception ex)
			{
				Log.Info("ANIM", $"Transition failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
				incoming?.Release();
				outgoing?.Release();
				_isAnimating = false;
				_overlay?.Hide();
				tcs.TrySetResult(false);
			}

			return tcs.Task;
		}

		/// <summary>
		/// Drives both cards from a per-frame rendering tick over <see cref="AnimationDurationMs"/>.
		/// A Storyboard can move the host rect but can't animate the perspective matrix,
		/// so size + 3D tilt are interpolated here and pushed to the live session each frame.
		/// </summary>
		private void RunFlight(TaskCompletionSource<bool> tcs,
			IFlyingCard? incoming, Rect inFrom, Rect inTo,
			IFlyingCard? outgoing, Rect outFrom, Rect outTo)
		{
			var easing = new PowerEase { EasingMode = EasingMode.EaseOut };
			double durationMs = AnimationDurationMs;
			TimeSpan? start = null;
			EventHandler? handler = null;

			void Finish(bool ok)
			{
				CompositionTarget.Rendering -= handler;
				incoming?.Release();
				outgoing?.Release();
				if (_overlay != null && _overlay.Canvas.Children.Count == 0) _overlay.Hide();
				_isAnimating = false;
				tcs.TrySetResult(ok);
			}

			handler = (s, e) =>
			{
				// A throw inside the rendering tick must still unsubscribe + release,
				// otherwise the handler leaks and _isAnimating stays true forever.
				try
				{
					var now = ((RenderingEventArgs)e).RenderingTime;
					start ??= now;
					double u = Math.Clamp((now - start.Value).TotalMilliseconds / durationMs, 0.0, 1.0);
					double k = easing.Ease(u);

					incoming?.Update(LerpRect(inFrom, inTo, k), Lerp(CompositionThumbnail.TrayTiltDegrees, 0.0, k));
					outgoing?.Update(LerpRect(outFrom, outTo, k), Lerp(0.0, CompositionThumbnail.TrayTiltDegrees, k));

					if (u >= 1.0)
					{
						Log.Info("ANIM", "Flight completed");
						Finish(true);
					}
				}
				catch (Exception ex)
				{
					Log.Info("ANIM", $"Flight tick failed, aborting: {ex.Message}");
					Finish(false);
				}
			};

			CompositionTarget.Rendering += handler;
		}

		[System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_overlay))]
		private void EnsureOverlay(Rect bounds)
		{
			_overlay ??= new TransitionOverlayWindow();
			_overlay.PositionFrom(bounds);
		}

		private static double Lerp(double a, double b, double t) => DragDropManager.Lerp(a, b, t);

		private static Rect LerpRect(Rect a, Rect b, double t) =>
			new Rect(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Width, b.Width, t), Lerp(a.Height, b.Height, t));

		private static string Fmt(Rect r) => $"({r.X:F0},{r.Y:F0} {r.Width:F0}x{r.Height:F0})";

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
