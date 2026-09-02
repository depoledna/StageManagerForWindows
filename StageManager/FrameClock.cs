using System.Diagnostics;
using System.Threading;
using System.Windows.Media;

namespace StageManager
{
	/// <summary>
	/// Counts composition frames so a log line can say which frame it happened on.
	/// <para>
	/// The wall clock cannot answer "did these two things reach the screen together".
	/// A frame is 8 ms at 120 Hz, so two calls a millisecond apart may still be composed
	/// a frame apart, and two calls ten milliseconds apart may not be. The frame number
	/// is what settles it; the millisecond timings alongside it say where the time went.
	/// </para>
	/// <para>
	/// Only logging reads this. <see cref="Start"/> is DEBUG-only, so in Release the
	/// counter stays at zero and nothing subscribes to the render loop.
	/// </para>
	/// </summary>
	internal static class FrameClock
	{
		private static long _frame;

		/// <summary>Composition frames since <see cref="Start"/>. Zero in Release.</summary>
		public static long Frame => Interlocked.Read(ref _frame);

		[Conditional("DEBUG")]
		public static void Start()
		{
			CompositionTarget.Rendering += (s, e) => Interlocked.Increment(ref _frame);
		}
	}
}
