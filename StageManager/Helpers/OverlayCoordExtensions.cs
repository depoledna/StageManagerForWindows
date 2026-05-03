using System.Windows;

namespace StageManager.Helpers
{
	/// <summary>
	/// Extension methods for translating between screen coords and overlay-canvas coords.
	/// All transparent topmost overlays in the app place children via Canvas.SetLeft/Top
	/// relative to the overlay window, so screen-space inputs (cursor pos, real-window
	/// rect, sidebar item rect) need a `- overlay.Left/Top` shift.
	/// </summary>
	public static class OverlayCoordExtensions
	{
		/// <summary>Translate a screen-space point to the overlay's canvas space.</summary>
		public static Point ToCanvas(this Point screenPoint, Window overlay) =>
			new Point(screenPoint.X - overlay.Left, screenPoint.Y - overlay.Top);

		/// <summary>Translate a screen-space rect to the overlay's canvas space (origin only — size unchanged).</summary>
		public static Rect ToCanvas(this Rect screenRect, Window overlay) =>
			new Rect(screenRect.X - overlay.Left, screenRect.Y - overlay.Top, screenRect.Width, screenRect.Height);

		/// <summary>Sets Left/Top/Width/Height on a window from a Rect in one call.</summary>
		public static void PositionFrom(this Window window, Rect bounds)
		{
			window.Left = bounds.X;
			window.Top = bounds.Y;
			window.Width = bounds.Width;
			window.Height = bounds.Height;
		}
	}
}
