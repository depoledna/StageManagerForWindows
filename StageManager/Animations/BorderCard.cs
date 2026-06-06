using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StageManager.Helpers;

namespace StageManager.Animations
{
	/// <summary>
	/// Static fallback flying card: the icon placeholder Border on the overlay
	/// canvas. Used when no live capture session is available for a card. The 3D
	/// tilt is approximated by a horizontal scale (ScaleX = cos θ), matching the
	/// rest of the proxy animations since .NET 10 WPF dropped PlaneProjection.
	/// </summary>
	internal sealed class BorderCard : IFlyingCard
	{
		private readonly TransitionOverlayWindow _overlay;
		private readonly Border _border;

		public BorderCard(TransitionOverlayWindow overlay, ImageSource? icon)
		{
			_overlay = overlay;
			_border = PlaceholderFactory.Create(icon);
			_overlay.Canvas.Children.Add(_border);
		}

		public void Update(Rect baseRect, double skewDegrees)
		{
			var c = baseRect.ToCanvas(_overlay);
			Canvas.SetLeft(_border, c.X);
			Canvas.SetTop(_border, c.Y);
			_border.Width = Math.Max(1, c.Width);
			_border.Height = Math.Max(1, c.Height);
			if (_border.RenderTransform is ScaleTransform st)
				st.ScaleX = Math.Cos(skewDegrees * Math.PI / 180.0);
		}

		public void SetVisible(bool visible) =>
			_border.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

		public void Release() => _overlay.Canvas.Children.Remove(_border);
	}
}
