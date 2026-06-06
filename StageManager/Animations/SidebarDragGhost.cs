using System;
using System.Linq;
using System.Windows;
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
			var overlay = _animator.Overlay;
			_card?.Release();
			_card = null;
			if (overlay is not null && overlay.Canvas.Children.Count == 0)
				overlay.Hide();
			_isActive = false;
			Log.Info("DRAG", "Ghost hidden");
		}
	}
}
