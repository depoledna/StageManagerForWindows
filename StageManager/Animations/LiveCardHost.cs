using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using StageManager.Composition;
using StageManager.Controls;
using StageManager.Helpers;

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
			_session.SetTransformMatrix(CompositionThumbnail.ComposeTransform(skewDegrees, 1.0, 0, 0, hwndWpx, hwndHpx));
		}

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
				_host.Root = null;
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
