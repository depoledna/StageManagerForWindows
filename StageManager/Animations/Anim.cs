using System.Windows;
using System.Windows.Media.Animation;

namespace StageManager.Animations
{
	/// <summary>
	/// Factory for <see cref="DoubleAnimation"/> instances. Replaces inline
	/// `new DoubleAnimation(...) { EasingFunction = ..., FillBehavior = ... }`
	/// patterns scattered across the codebase.
	/// </summary>
	public static class Anim
	{
		/// <summary>Animate from current property value to <paramref name="to"/>.</summary>
		public static DoubleAnimation To(double to, Duration duration,
			IEasingFunction? easing = null, FillBehavior fillBehavior = FillBehavior.HoldEnd)
			=> new DoubleAnimation(to, duration) { EasingFunction = easing, FillBehavior = fillBehavior };

		/// <summary>Animate from explicit <paramref name="from"/> to <paramref name="to"/>.</summary>
		public static DoubleAnimation From(double from, double to, Duration duration,
			IEasingFunction? easing = null, FillBehavior fillBehavior = FillBehavior.HoldEnd)
			=> new DoubleAnimation(from, to, duration) { EasingFunction = easing, FillBehavior = fillBehavior };

		/// <summary>
		/// Animate from→to with target+property pre-bound for storyboard composition.
		/// </summary>
		public static DoubleAnimation Storyboard(double from, double to, Duration duration,
			IEasingFunction easing, UIElement target, DependencyProperty property)
		{
			var anim = new DoubleAnimation(from, to, duration) { EasingFunction = easing };
			System.Windows.Media.Animation.Storyboard.SetTarget(anim, target);
			System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new PropertyPath(property));
			return anim;
		}
	}
}
