using System;
using System.Runtime.InteropServices;

namespace StageManager.Composition
{
	/// <summary>
	/// Ensures a <c>Windows.System.DispatcherQueueController</c> exists on the
	/// current thread. Required so a <c>Windows.UI.Composition.Compositor</c>
	/// can run on the UI thread (the compositor needs a dispatcher queue to
	/// schedule animation callbacks).
	/// </summary>
	internal static class DispatcherQueueHelper
	{
		// DQTYPE_THREAD_CURRENT
		private const int DQTYPE_THREAD_CURRENT = 2;
		// DQTAT_COM_STA
		private const int DQTAT_COM_STA = 2;

		[StructLayout(LayoutKind.Sequential)]
		private struct DispatcherQueueOptions
		{
			public int dwSize;
			public int threadType;
			public int apartmentType;
		}

		[DllImport("coremessaging.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = false)]
		private static extern void CreateDispatcherQueueController(
			DispatcherQueueOptions options,
			[MarshalAs(UnmanagedType.IUnknown)] out object dispatcherQueueController);

		// One controller per thread. Holding the reference keeps the queue alive
		// for the lifetime of the thread.
		[ThreadStatic]
		private static object? _controller;

		/// <summary>
		/// Creates a dispatcher queue controller on the current thread if one
		/// does not already exist. Idempotent.
		/// </summary>
		public static void EnsureOnCurrentThread()
		{
			if (_controller is not null)
				return;

			var options = new DispatcherQueueOptions
			{
				dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
				threadType = DQTYPE_THREAD_CURRENT,
				apartmentType = DQTAT_COM_STA,
			};

			CreateDispatcherQueueController(options, out var controller);
			_controller = controller;
		}
	}
}
