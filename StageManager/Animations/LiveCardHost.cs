using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using StageManager.Composition;
using StageManager.Controls;
using StageManager.Helpers;
using StageManager.Model;

namespace StageManager.Animations
{
	/// <summary>
	/// A flying card backed by a live Windows.Graphics.Capture session, re-hosted on
	/// the transition overlay so the very frames the user sees travel and skew with
	/// the card. Two flavours:
	/// <list type="bullet">
	/// <item><b>Borrowed</b> — lends a tray tile's running session (<see cref="TryBorrow"/>);
	/// <see cref="Release"/> hands the visual back to the tile.</item>
	/// <item><b>Owned</b> — spins up a fresh session for a window with no tray tile
	/// (<see cref="TryCreateOwned"/>, e.g. the current scene leaving the stage);
	/// <see cref="Release"/> disposes it.</item>
	/// </list>
	/// </summary>
	internal sealed class LiveCardHost : IFlyingCard
	{
		// Per-side HWND inflation so the perspective-skewed near edge isn't clipped.
		// Single source of truth with the tray tile so the host rect matches at handoff.
		private const double Headroom = CompositionThumbnail.HoverHeadroom;

		private readonly TransitionOverlayWindow _overlay;
		private readonly Point _dpi;
		private readonly double _cornerRadius;

		private CompositionHost? _host;
		private CompositionThumbnail? _borrowedTile;
		private CaptureSession? _ownedSession;
		private CaptureSession? _session;

		// The tray tile's resting edge angles, captured at borrow. The card interpolates
		// these to (0,0) as it flies to the stage, re-solving the sprite rotation against
		// its CURRENT height every frame — see SetEdgeShape. Unset for owned cards, which
		// have no tile to inherit from and read the tilt law directly instead.
		private double _restTopEdgeDegrees;
		private double _restBottomEdgeDegrees;
		private double _lastAppliedRotationDegrees = double.NaN;

		private LiveCardHost(TransitionOverlayWindow overlay, Point dpi, double cornerRadius)
		{
			_overlay = overlay;
			_dpi = new Point(dpi.X <= 0 ? 1 : dpi.X, dpi.Y <= 0 ? 1 : dpi.Y);
			_cornerRadius = cornerRadius;
		}

		/// <summary>Borrow a tray tile's live visual, or null if it has no session yet.</summary>
		public static LiveCardHost? TryBorrow(TransitionOverlayWindow overlay, CompositionThumbnail? tile, Point dpi, double cornerRadius)
		{
			var visual = tile?.BorrowRootVisual();
			if (visual is null || tile!.Session is null) return null;

			var card = new LiveCardHost(overlay, dpi, cornerRadius)
			{
				_borrowedTile = tile,
				_session = tile.Session,
				// Inherit the tile's resting trapezoid so the card leaves the tray with
				// exactly the tile's shape (no pop at handoff) and unfolds from there.
				_restTopEdgeDegrees = tile.TopEdgeDegrees,
				_restBottomEdgeDegrees = tile.BottomEdgeDegrees,
			};
			card.Mount(visual);
			return card;
		}

		/// <summary>Capture a window that has no tray tile into a fresh owned session.</summary>
		public static LiveCardHost? TryCreateOwned(TransitionOverlayWindow overlay, IntPtr hwnd, Point dpi, double cornerRadius)
		{
			if (hwnd == IntPtr.Zero) return null;

			try
			{
				var card = new LiveCardHost(overlay, dpi, cornerRadius);
				card._host = new CompositionHost();
				overlay.Canvas.Children.Add(card._host);

				var compositor = card._host.Compositor;
				var devices = D3DDeviceHolder.GetOrCreate(compositor);
				var session = new CaptureSession(hwnd, compositor, devices);
				session.Start();

				card._ownedSession = session;
				card._session = session;
				card._host.Root = session.RootVisual;
				// Same seeding Mount does for a borrowed visual. Without it the owned
				// session has no camera, the sprite's Y-rotation has nothing to divide
				// against, and the card renders dead flat however it is skewed.
				session.SetPerspective((float)CompositionThumbnail.PerspectiveDepthPx);
				return card;
			}
			catch (Exception ex)
			{
				Log.Info("ANIM", $"LiveCardHost owned-capture failed for 0x{hwnd:X}: {ex.Message}");
				return null;
			}
		}

		private void Mount(Windows.UI.Composition.Visual visual)
		{
			_host = new CompositionHost();
			_overlay.Canvas.Children.Add(_host);
			_host.Root = visual;

			// The tile normally seeds this, but state it once here so the card's camera
			// never depends on the tile having run a size pass. Constant for the whole
			// flight — SetEdgeShape only varies the sprite rotation against it.
			_session?.SetPerspective((float)CompositionThumbnail.PerspectiveDepthPx);
		}

		public void Update(Rect baseRect, double skewDegrees)
		{
			if (_host is null || _session is null) return;

			// Oversize the host (centred on the base rect) so the skewed near edge
			// has slack inside the child HWND.
			double hostW = baseRect.Width * (1.0 + 2.0 * Headroom);
			double hostH = baseRect.Height * (1.0 + 2.0 * Headroom);
			double cx = baseRect.X + baseRect.Width / 2.0;
			double cy = baseRect.Y + baseRect.Height / 2.0;
			var canvas = new Point(cx - hostW / 2.0, cy - hostH / 2.0).ToCanvas(_overlay);

			Canvas.SetLeft(_host, canvas.X);
			Canvas.SetTop(_host, canvas.Y);
			_host.Width = hostW;
			_host.Height = hostH;

			float baseWpx = (float)(baseRect.Width * _dpi.X);
			float baseHpx = (float)(baseRect.Height * _dpi.Y);
			float hwndWpx = (float)(hostW * _dpi.X);
			float hwndHpx = (float)(hostH * _dpi.Y);

			_session.SetVisualSize(new Vector2(hwndWpx, hwndHpx), new Vector2(baseWpx, baseHpx));
			_session.SetCornerRadius((float)(_cornerRadius * _dpi.X), new Vector2(baseWpx, baseHpx));
			SetEdgeShape(skewDegrees, baseRect, baseHpx, hwndWpx, hwndHpx);
		}

		/// <summary>
		/// The pair of resting edge angles this card interpolates toward the tray.
		/// A borrowed card inherits its tray tile's, captured at borrow. An owned card
		/// has no tile, so it reads the tray tilt law at its own current edges — the same
		/// law that gave the tile its angles, evaluated where the card actually is.
		/// </summary>
		private (double Top, double Bottom) RestEdgeDegrees(Rect baseRect)
			=> _borrowedTile is not null
				? (_restTopEdgeDegrees, _restBottomEdgeDegrees)
				: (SceneModel.EdgeTiltDegreesAt(baseRect.Top), SceneModel.EdgeTiltDegreesAt(baseRect.Bottom));

		/// <summary>
		/// Re-solves the card's trapezoid against its CURRENT height, every frame.
		/// <para>
		/// This is the whole point. The sprite's native Y-rotation and the root's fixed
		/// <see cref="CompositionThumbnail.PerspectiveDepthPx"/> vanishing distance
		/// produce convergence Q = H*tan(theta)/(2*depth) — Q grows with the sprite. The
		/// card previously kept the rotation the tray tile solved for a ~200px-tall
		/// sprite while growing to a ~1600px-tall one, so the perspective divide ran
		/// away: at stage size one vertical edge scaled ~1.85x and the other ~0.69x, a
		/// 2.7:1 trapezoid taller than the display. Solving from the live height keeps
		/// the requested edge angles exact at every size, which is exactly what
		/// CompositionThumbnail does for the resting tile.
		/// </para>
		/// </summary>
		private void SetEdgeShape(double skewDegrees, Rect baseRect, float baseHpx, float hwndWpx, float hwndHpx)
		{
			if (_session is null) return;

			// skewDegrees carries how much "tray-ness" the card has: TrayTiltDegrees at
			// the tray end of the flight, 0 at the stage end. Reuse it as the interpolation
			// fraction for the resting edge angles so the trapezoid folds and unfolds in
			// lockstep with the flight — square on the stage, full tray shape at the strip.
			// Both directions run this same solve; the stage→tray card used to take a
			// shortcut that fed skewDegrees in as a raw shear, which is a parallelogram,
			// not a trapezoid, and at 2° it read as no tilt at all.
			double trayFraction = CompositionThumbnail.TrayTiltDegrees > 0.0
				? Math.Clamp(skewDegrees / CompositionThumbnail.TrayTiltDegrees, 0.0, 1.0)
				: 0.0;

			var rest = RestEdgeDegrees(baseRect);
			var (shearDegrees, rotationDegrees) = CompositionThumbnail.SolveEdgeAngles(
				rest.Top * trayFraction,
				rest.Bottom * trayFraction,
				baseHpx);

			// Dirty-check: one WinRT interop call per frame per card saved whenever the
			// rotation is unchanged (notably the flat owned card, where it stays 0).
			if (rotationDegrees != _lastAppliedRotationDegrees)
			{
				_lastAppliedRotationDegrees = rotationDegrees;
				_session.SetSpriteRotationY((float)rotationDegrees);
			}

			_session.SetTransformMatrix(
				CompositionThumbnail.ComposeTransform(shearDegrees, 1.0, hwndWpx, hwndWpx, hwndHpx));
		}

		// A borrowed session has been running behind a tray tile and is already showing
		// frames; an owned one was started microseconds ago and is still blank.
		public bool HasContent => _session?.HasFrame ?? false;

		public void SetVisible(bool visible)
		{
			if (_host is not null)
				_host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		}

		public void Release()
		{
			if (_host is not null)
			{
				// Detach the visual from the overlay BEFORE returning/disposing, so
				// tearing down the overlay host can't take the tile's visual with it.
				// Detach can throw E_INVALIDARG if the visual died under us (device
				// lost, session torn down off-thread) — never let that kill the app.
				try { _host.Root = null; }
				catch (Exception ex) { Log.Info("ANIM", $"LiveCardHost detach threw: {ex.Message}"); }
				_borrowedTile?.ReturnRootVisual();
				_overlay.Canvas.Children.Remove(_host);
				try { _host.Dispose(); }
				catch (Exception ex) { Log.Info("ANIM", $"LiveCardHost dispose threw: {ex.Message}"); }
				_host = null;
			}

			if (_ownedSession is not null)
			{
				try { _ownedSession.Dispose(); }
				catch (Exception ex) { Log.Info("ANIM", $"LiveCardHost owned-session dispose threw: {ex.Message}"); }
				_ownedSession = null;
			}

			_borrowedTile = null;
			_session = null;
		}
	}
}
