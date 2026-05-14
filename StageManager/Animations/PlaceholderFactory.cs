using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace StageManager.Animations
{
	internal static class PlaceholderFactory
	{
		private static readonly Color Background = Color.FromArgb(220, 240, 240, 240);
		private const double CornerRadiusValue = 8;
		private const double IconSize = 48;
		private const double ShadowBlurRadius = 10;
		private const double ShadowDepthValue = 2;
		private const double ShadowOpacity = 0.4;

		public static Border Create(ImageSource? icon)
		{
			return new Border
			{
				Background = new SolidColorBrush(Background),
				CornerRadius = new CornerRadius(CornerRadiusValue),
				ClipToBounds = false,
				Child = new Image
				{
					Source = icon,
					Width = IconSize,
					Height = IconSize,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
				},
				Effect = new DropShadowEffect
				{
					BlurRadius = ShadowBlurRadius,
					ShadowDepth = ShadowDepthValue,
					Opacity = ShadowOpacity,
				},
			};
		}
	}
}
