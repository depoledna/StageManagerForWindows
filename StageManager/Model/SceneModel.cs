using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace StageManager.Model
{
	[System.Diagnostics.DebuggerDisplay("{Title}")]
	public class SceneModel : INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler? PropertyChanged;
		private bool _isVisible;
		private Scene _scene = null!;

		public static SceneModel FromScene(Scene scene)
		{
			var model = new SceneModel();
			model.Id = scene.Id;
			model.Windows = new ObservableCollection<WindowModel>(scene.Windows.Select(w => new WindowModel(w)));
			model.Scene = scene;
			// Initial preview size calculation
			model.UpdatePreviewSizes();
			return model;
		}

		public SceneModel()
		{
			Updated = DateTime.UtcNow;
		}

		public void UpdateFromScene(Scene updatedScene)
		{
			if (Id != updatedScene.Id)
				throw new NotSupportedException();

			Scene = updatedScene;

			var updatedWindows = updatedScene.Windows.ToArray();
			for (int i = 0; i < updatedWindows.Length; i++)
			{
				if (Windows.Count > i && Windows[i].Window.Handle == updatedWindows[i].Handle)
				{
					// same position - just update
					Windows[i].Window = updatedWindows[i];
				}
				else
				{
					var windowToUpdate = Windows.FirstOrDefault(w => w.Window.Handle == updatedWindows[i].Handle);
					if (windowToUpdate is object)
					{
						// has the window but other position -> update and move
						windowToUpdate.Window = updatedWindows[i];
						Windows.Move(Windows.IndexOf(windowToUpdate), i);
					}
					else
					{
						// no window tp update --> add/insert
						Windows.Insert(i, new WindowModel(updatedWindows[i]));
					}
				}
			}

			// remove windows that have been gone
			if (Windows.Count > updatedScene.Windows.Count())
			{
				for (int i = Windows.Count - 1; i >= 0; i--)
				{
					if (!updatedScene.Windows.Any(w => w.Handle == Windows[i].Window.Handle))
						Windows.RemoveAt(i);
				}
			}

			Updated = DateTime.UtcNow;
			// Re-calculate scaled thumbnail sizes after the window set/positions changed.
			UpdatePreviewSizes();
		}

		#region Thumbnail scaling
		/// <summary>
		/// Recalculates <see cref="WindowModel.PreviewWidth"/> and <see cref="WindowModel.PreviewHeight"/> for all
		/// windows in this scene so that previews keep their relative size ratio. The largest window’s preview
		/// will have a width of 120 px (baseline). The same scale factor is applied uniformly to height so that
		/// aspect ratios are preserved.
		/// </summary>
		public void UpdatePreviewSizes()
		{
			if (Windows is null || !Windows.Any())
				return;

			// Determine sizes, considering minimized windows (use normal bounds via GetWindowPlacement)
			var sizes = Windows.Select(w => GetWindowSize(w.Window)).ToArray();

			var maxWidth = sizes.Max(s => s.width);
			if (maxWidth <= 0)
				maxWidth = 1;

			var scale = 180.0 / maxWidth; // baseline width of 180 px

			for (int i = 0; i < Windows.Count; i++)
			{
				var (origWidth, origHeight) = sizes[i];
				var newWidth = Math.Max(50, origWidth * scale);
				var newHeight = Math.Max(50, origHeight * scale);

				var window = Windows[i];
				window.PreviewWidth = newWidth;
				window.PreviewHeight = newHeight;

				System.Diagnostics.Debug.WriteLine($"[ThumbnailScale] Scene '{Title}' – Window '{window.Title}' original={origWidth}x{origHeight} => preview={newWidth:F0}x{newHeight:F0} (scale={scale:F3})");
			}
		}

		private static (int width, int height) GetWindowSize(StageManager.Native.Window.IWindow window)
		{
			if (window is StageManager.Native.WindowsWindow ww)
			{
				// If minimized, attempt to query normal (restored) bounds via GetWindowPlacement
				if (ww.IsMinimized)
				{
					var rc = GetNormalBounds(ww.Handle);
					if (rc.Width > 0 && rc.Height > 0)
						return (rc.Width, rc.Height);
				}
			}

			var loc = window.Location;
			return (loc.Width, loc.Height);
		}

		#region Native helpers
		[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
		private struct WINDOWPLACEMENT
		{
			public int length;
			public int flags;
			public int showCmd;
			public System.Drawing.Point ptMinPosition;
			public System.Drawing.Point ptMaxPosition;
			public StageManager.Native.PInvoke.Win32.Rect rcNormalPosition;
		}

		private static System.Drawing.Rectangle GetNormalBounds(IntPtr hwnd)
		{
			var wp = new WINDOWPLACEMENT();
			wp.length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINDOWPLACEMENT));
			if (GetWindowPlacement(hwnd, ref wp))
			{
				var rc = wp.rcNormalPosition;
				return System.Drawing.Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
			}
			return System.Drawing.Rectangle.Empty;
		}

		[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
		[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
		private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
		#endregion
		#endregion

		private void Scene_SelectedChanged(object? sender, EventArgs e)
		{
			Updated = DateTime.UtcNow;
			UpdatePreviewSizes();
		}

		public Guid Id { get; set; }

		public Scene Scene
		{
			get => _scene;
			private set
			{
				if (_scene is object)
					_scene.SelectedChanged -= Scene_SelectedChanged;

				_scene = value;

				if (_scene is object)
					_scene.SelectedChanged += Scene_SelectedChanged;
			}
		}

		public string Title => Scene?.Title ?? "";

		public bool IsVisible
		{
			get => _isVisible;
			set
			{
				if (_isVisible != value)
				{
					_isVisible = value;
					RaisePropertyChanged();
					RaisePropertyChanged(nameof(Visibility));
				}
			}
		}

		private double _tiltAngleDegrees;
		/// <summary>
		/// Per-row vertical-shear skew (degrees) for the live thumbnail, set from the
		/// scene's vertical position in the tray (top leans down, bottom up, middle
		/// flat) by MainWindow.AssignRowTilts. Bound to CompositionThumbnail.SkewAngleDegrees.
		/// </summary>
		public double TiltAngleDegrees
		{
			get => _tiltAngleDegrees;
			set
			{
				if (_tiltAngleDegrees != value)
				{
					_tiltAngleDegrees = value;
					RaisePropertyChanged();
				}
			}
		}

		public DateTime Updated { get; private set; }

		private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(memberName));
		}

		/// <summary>
		/// When true, the item is invisible but still occupies layout space (Hidden vs Collapsed).
		/// Used during scene-switch animation to prevent other items from shifting.
		/// </summary>
		public bool IsHiddenButReserved
		{
			get => _isHiddenButReserved;
			set
			{
				if (_isHiddenButReserved != value)
				{
					_isHiddenButReserved = value;
					RaisePropertyChanged(nameof(Visibility));
				}
			}
		}
		private bool _isHiddenButReserved;

		public System.Windows.Visibility Visibility =>
			IsVisible ? System.Windows.Visibility.Visible :
			IsHiddenButReserved ? System.Windows.Visibility.Hidden :
			System.Windows.Visibility.Collapsed;

		public ObservableCollection<WindowModel> Windows { get; set; } = new ObservableCollection<WindowModel>();
	}
}
