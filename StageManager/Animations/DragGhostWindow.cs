using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using StageManager.Controls;

namespace StageManager.Animations
{
	/// <summary>
	/// Manages a drag ghost overlay on a dedicated STA thread.
	/// WPF rendering on the overlay thread is independent of the main thread,
	/// so the ghost renders correctly even during Win32 modal drag loops.
	/// </summary>
	internal class DragGhostWindow : IDisposable
	{
		private Thread _thread;
		private Dispatcher _dispatcher;
		private readonly ManualResetEventSlim _ready = new();
		private Window _window;
		private volatile bool _disposed;

		public DragGhostWindow()
		{
			_thread = new Thread(RunOverlayThread);
			_thread.SetApartmentState(ApartmentState.STA);
			_thread.IsBackground = true;
			_thread.Start();
			_ready.Wait();
		}

		private void RunOverlayThread()
		{
			try
			{
				_dispatcher = Dispatcher.CurrentDispatcher;

				_window = new LayeredOverlayWindowBase();

				// Signal ready before Dispatcher.Run — warmup happens via BeginInvoke
				_ready.Set();

				// Pre-create HWND so first Show has no lag
				_dispatcher.BeginInvoke(() =>
				{
					_window.Left = -10000;
					_window.Top = -10000;
					_window.Width = 1;
					_window.Height = 1;
					_window.Show();
					_window.Hide();
					Log.Info("DRAG-GHOST", "Overlay HWND warmed up");
				});

				Dispatcher.Run();
			}
			catch (Exception ex)
			{
				Log.Fatal("DRAG-GHOST", $"Overlay thread crashed: {ex}");
				_disposed = true;
				_ready.Set();
			}
		}

		/// <summary>
		/// Shows the drag ghost at the given logical coordinates with the specified icon.
		/// </summary>
		public void Show(double x, double y, double w, double h, ImageSource icon)
		{
			if (_disposed) return;

			// Freeze the icon so it can cross thread boundaries
			ImageSource frozenIcon = null;
			if (icon != null)
			{
				frozenIcon = icon.IsFrozen ? icon : icon.CloneCurrentValue();
				if (!frozenIcon.IsFrozen)
					frozenIcon.Freeze();
			}

			_dispatcher.BeginInvoke(() =>
			{
				_window.Content = PlaceholderFactory.Create(frozenIcon);
				_window.Left = x;
				_window.Top = y;
				_window.Width = w;
				_window.Height = h;
				_window.Show();
			});
		}

		/// <summary>
		/// Updates the ghost position and size. Coordinates are in WPF logical units.
		/// Called from PollTick on the main thread during modal drag — the overlay thread
		/// processes this independently and re-renders.
		/// </summary>
		public void Update(double x, double y, double w, double h)
		{
			if (_disposed) return;

			_dispatcher.BeginInvoke(() =>
			{
				if (_window == null) return;
				_window.Left = x;
				_window.Top = y;
				_window.Width = Math.Max(1, w);
				_window.Height = Math.Max(1, h);
			}, DispatcherPriority.Render);
		}

		/// <summary>
		/// Hides the drag ghost.
		/// </summary>
		public void Hide()
		{
			if (_disposed) return;

			_dispatcher.BeginInvoke(() =>
			{
				if (_window == null) return;
				_window.Hide();
				_window.Content = null;
			});
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			try
			{
				_dispatcher?.InvokeShutdown();
				_thread?.Join(1000);
			}
			catch { }

			_ready.Dispose();
		}
	}
}
