using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using StageManager.Controls;
using StageManager.Model;

namespace StageManager.Animations
{
	/// <summary>
	/// Drag ghost for the sidebar → active-screen flow. Borrows the tray tile's
	/// live capture (so the very frames the user sees travel and unskew with the
	/// cursor), falling back to a static icon card when the tile has no running
	/// session yet. Both paths share <see cref="IFlyingCard"/>.
	/// </summary>
	internal class SidebarDragGhost
	{
		private readonly SceneTransitionAnimator _animator;
		private IFlyingCard? _card;
		private bool _isActive;

		public bool IsActive => _isActive;

		public SidebarDragGhost(SceneTransitionAnimator animator)
		{
			_animator = animator;
		}

		public void Show(Rect overlayBounds, Rect ghostRect, SceneModel scene,
			CompositionThumbnail? tile, Point dpi, double cornerRadius)
		{
			if (_animator.IsAnimating) return;
			_isActive = true;

			try
			{
				var overlay = _animator.GetOrCreateOverlay(overlayBounds);
				_card = LiveCardHost.TryBorrow(overlay, tile, dpi, cornerRadius) as IFlyingCard
					?? new BorderCard(overlay, scene?.Windows.FirstOrDefault()?.Icon);
				_card.Update(ghostRect, CompositionThumbnail.TrayTiltDegrees);
				overlay.Show();
			}
			catch (Exception ex)
			{
				Log.Info("DRAG", $"ShowDragGhost failed: {ex.Message}");
				Hide();
			}
		}

		/// <summary>
		/// Owned variant for the stage→tray drag: the dragged window has no tray tile to
		/// borrow, so capture it into a fresh session. Starts flat (0° — it's on stage);
		/// the caller skews it toward the tray angle across the buffer. Falls back to a
		/// static icon card when capture is unavailable.
		/// </summary>
		public void ShowOwned(Rect overlayBounds, Rect ghostRect, IntPtr hwnd,
			ImageSource? icon, Point dpi, double cornerRadius)
		{
			if (_animator.IsAnimating) return;
			_isActive = true;

			try
			{
				var overlay = _animator.GetOrCreateOverlay(overlayBounds);
				_card = LiveCardHost.TryCreateOwned(overlay, hwnd, dpi, cornerRadius) as IFlyingCard
					?? new BorderCard(overlay, icon);
				_card.Update(ghostRect, 0.0);
				overlay.Show();
			}
			catch (Exception ex)
			{
				Log.Info("DRAG", $"ShowOwned failed: {ex.Message}");
				Hide();
			}
		}

		/// <summary>
		/// Position/size the ghost. (screenX, screenY) is the base rect's top-left
		/// in logical screen units; skewDegrees is the 3D Y-tilt (tray angle in the
		/// sidebar, lerped to 0 across the buffer).
		/// </summary>
		public void UpdatePositionAndSize(double screenX, double screenY, double width, double height, double skewDegrees)
		{
			_card?.Update(new Rect(screenX, screenY, Math.Max(1, width), Math.Max(1, height)), skewDegrees);
		}

		public void SetVisible(bool visible) => _card?.SetVisible(visible);

		public void Hide()
		{
			_card?.Release();
			_card = null;
			// The overlay itself stays shown — see SceneTransitionAnimator.WarmUp. Hiding
			// it here made the next scene switch pay a composition frame to show it again,
			// and that frame is one the tray tile spends already blanked by the borrow.
			_isActive = false;
			Log.Info("DRAG", "Ghost hidden");
		}
	}
}
