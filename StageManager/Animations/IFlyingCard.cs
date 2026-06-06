using System.Windows;

namespace StageManager.Animations
{
	/// <summary>
	/// A card that travels between the sidebar and the stage during a transition.
	/// Backed either by the tray tile's live capture (<see cref="LiveCardHost"/>) or
	/// a static icon placeholder (<see cref="BorderCard"/>). The driver (cursor drag
	/// or timed fly) calls <see cref="Update"/> each frame with the current rect and
	/// 3D tilt, then <see cref="Release"/> once when finished.
	/// </summary>
	internal interface IFlyingCard
	{
		/// <param name="baseRect">Card rect in logical screen units.</param>
		/// <param name="skewDegrees">3D Y-tilt (tray angle in the sidebar, 0 flat on stage).</param>
		void Update(Rect baseRect, double skewDegrees);

		void SetVisible(bool visible);

		void Release();
	}
}
