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

		// Composition frames the cards get to become presentable before the caller is
		// allowed to hide anything real. The first tick covers the overlay's own commit;
		// the rest are slack for a fresh capture session's first frame. Capped so a window
		// that never produces one (capture denied, fully occluded) can't stall the switch.
		private const int MaxReadyFrames = 5;

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
		/// It is shown and left shown for the process lifetime: it is click-through
		/// (WS_EX_TRANSPARENT), never activated and empty between transitions, and showing
		/// a layered transparent window costs a composition frame that would otherwise be
		/// spent with the tray tile already blanked by the borrow.
		/// </summary>
		public void WarmUp(Rect bounds)
		{
			EnsureOverlay(bounds);
			_overlay.Show();
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
		/// <para>
		/// <paramref name="onCardsReady"/> is where the caller hides the real content the
		/// cards replace — the stage windows and the tray tile. It is deliberately not the
		/// caller's job to do that up front: the cards need an arrange pass to build their
		/// child HWNDs and a capture frame to have any pixels, and hiding before that left
		/// the stage empty for two frames and the tray tile for one.
		/// </para>
		/// <para>
		/// <paramref name="onFlightLanded"/> is the same contract at the other end: the
		/// cards have arrived and are still on screen, so this is where the caller puts the
		/// real content back. It is awaited, so a caller that needs the rebuilt tray tile to
		/// have a capture frame can wait for it before the cards are dropped.
		/// </para>
		/// </summary>
		public async Task AnimateSceneTransitionAsync(
			Rect overlayBounds,
			Rect incomingSource, Rect incomingTarget, SceneModel incomingScene, CompositionThumbnail? incomingTile,
			Rect outgoingSource, Rect outgoingTarget, SceneModel? outgoingScene, IntPtr outgoingHandle,
			Point dpi, double cornerRadius,
			Action? onCardsReady = null, Func<Task>? onFlightLanded = null)
		{
			if (_isAnimating) return;
			_isAnimating = true;

			// Hoisted so the finally can release cards already added to the overlay.
			IFlyingCard? incoming = null;
			IFlyingCard? outgoing = null;

			// The caller's hide path must run exactly once. The catch below is also a
			// caller-must-still-hide path, and it can be reached after the normal call.
			var cardsReadySignalled = false;
			void SignalCardsReady()
			{
				if (cardsReadySignalled) return;
				cardsReadySignalled = true;
				onCardsReady?.Invoke();
			}

			// Likewise for the landing path, and for the same reason: a transition that
			// fails part-way still has to finish the switch, or the caller is left with
			// content hidden behind cards that are about to be released. The flag also
			// stops a callback that threw half-way from being retried on a mangled state.
			var flightLandedSignalled = false;
			async Task SignalFlightLanded()
			{
				if (flightLandedSignalled) return;
				flightLandedSignalled = true;
				if (onFlightLanded is not null) await onFlightLanded();
			}

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

				// Force the arrange pass NOW. Adding a CompositionHost to the canvas does
				// not create its child HWND — HwndHost.BuildWindowCore only runs during
				// arrange, and until it does CompositionHost.Root just parks the visual in
				// _pendingRoot. Without this the entire card tree mounts a frame late.
				_overlay.UpdateLayout();

				// The Update calls above ran before those HWNDs existed, so their canvas
				// offsets and host sizes had nothing to land on. Re-apply.
				incoming.Update(incomingSource, CompositionThumbnail.TrayTiltDegrees);
				outgoing?.Update(outgoingSource, 0.0);

				// Cards are mounted and sitting exactly on top of what they stand in for,
				// so handing over is invisible from here on.
				await WaitForCardsAsync(incoming, outgoing);
				SignalCardsReady();

				await RunFlightAsync(
					incoming, incomingSource, incomingTarget,
					outgoing, outgoingSource, outgoingTarget);

				// Same handover as at the start, run backwards. The cards are parked exactly
				// on their target rects, so this is where the real content comes back —
				// BEFORE they are released in the finally. Releasing first unmounted the
				// visuals a frame or more ahead of SwitchTo's SetWindowPos reaching the
				// compositor, which is the flash at the end of the switch.
				await SignalFlightLanded();
			}
			catch (Exception ex)
			{
				Log.Info("ANIM", $"Transition failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
				// The overlay stays shown even on failure — see WarmUp. Both callbacks still
				// have to run: whatever went wrong, the caller's hide and switch are what
				// leave the app in a coherent state once the cards are gone.
				SignalCardsReady();
				try { await SignalFlightLanded(); }
				catch (Exception inner) { Log.Info("ANIM", $"Landing callback also failed: {inner.Message}"); }
			}
			finally
			{
				incoming?.Release();
				outgoing?.Release();
				_isAnimating = false;
			}
		}

		/// <summary>
		/// Waits out composition frames until every card reports it has pixels, bounded by
		/// <see cref="MaxReadyFrames"/>. At least one frame always elapses: that is the one
		/// the overlay needs to commit its newly mounted visuals.
		/// </summary>
		private static Task WaitForCardsAsync(IFlyingCard? incoming, IFlyingCard? outgoing)
			=> WaitForAsync(
				() => (incoming?.HasContent ?? true) && (outgoing?.HasContent ?? true),
				"cards");

		/// <summary>
		/// Lets composition frames pass until <paramref name="ready"/> holds, bounded by
		/// <see cref="MaxReadyFrames"/>. At least one frame always elapses — that is the one
		/// the compositor needs to commit whatever was just mounted. Shared with the caller's
		/// landing path so both ends of a transition wait the same bounded way.
		/// </summary>
		internal static async Task WaitForAsync(Func<bool> ready, string what)
		{
			for (var i = 0; i < MaxReadyFrames; i++)
			{
				await NextCompositionFrameAsync();
				if (ready())
				{
					if (i > 0) Log.Info("ANIM", $"Waited {i + 1} frames for {what}");
					return;
				}
			}
			Log.Info("ANIM", $"Gave up waiting for {what} after {MaxReadyFrames} frames, proceeding");
		}

		private static Task NextCompositionFrameAsync()
		{
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			EventHandler? tick = null;
			tick = (s, e) =>
			{
				CompositionTarget.Rendering -= tick;
				tcs.TrySetResult(true);
			};
			CompositionTarget.Rendering += tick;
			return tcs.Task;
		}

		/// <summary>
		/// Drives both cards from a per-frame rendering tick over <see cref="AnimationDurationMs"/>.
		/// A Storyboard can move the host rect but can't animate the perspective matrix,
		/// so size + 3D tilt are interpolated here and pushed to the live session each frame.
		/// </summary>
		/// <remarks>
		/// Completes when the cards reach their targets. It deliberately does NOT release
		/// them — they have to stay on screen until the caller has put the real content
		/// back, which is the caller's job once this task completes.
		/// </remarks>
		private Task RunFlightAsync(
			IFlyingCard? incoming, Rect inFrom, Rect inTo,
			IFlyingCard? outgoing, Rect outFrom, Rect outTo)
		{
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var easing = new PowerEase { EasingMode = EasingMode.EaseOut };
			double durationMs = AnimationDurationMs;
			TimeSpan? start = null;
			EventHandler? handler = null;

			void Finish(bool ok)
			{
				CompositionTarget.Rendering -= handler;
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
			return tcs.Task;
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
