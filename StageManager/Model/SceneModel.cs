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
		/// macOS card sizing law, measured off macOS 26.5.2: every
		/// card is its source window under ONE uniform scale,
		/// s = max(0.135693, 96 / sourceHeightDip) — no per-scene normalization and
		/// no fitting into a box. The 96 dip floor (preferredMinimumItemHeight)
		/// scales small/short windows UP so no card ever ends thinner than 96 dip;
		/// aspect is always preserved.
		/// </summary>
		private const double BaseCardScale = 0.135693;
		private const double MinCardHeightDip = 96.0;
		// Same perspective distance the tilt law uses: d = 1379 on a 1169 pt
		// screen, scaled with monitor height.
		private const double EdgePerspectiveDistanceRatio = 1379.0 / 1169.0;

		public void UpdatePreviewSizes()
		{
			if (Windows is null || !Windows.Any())
				return;

			double dipScale = GetPixelsPerDip();

			foreach (var window in Windows)
			{
				// Minimized windows report their restored bounds via GetWindowPlacement.
				var (pxW, pxH) = GetWindowSize(window.Window);
				if (pxW <= 0 || pxH <= 0)
					continue;

				double wDip = pxW / dipScale;
				double hDip = pxH / dipScale;
				double s = Math.Max(BaseCardScale, MinCardHeightDip / hDip);
				double cardW = wDip * s;
				double cardH = hDip * s;

				// macOS anchors the card's perspective at its LEFT edge
				// (y' = Y − u(Y − pivot)/d), so the mid-column height is
				// H·(1 − W/(2d)) while the width stays W. Our renderer converges
				// symmetrically AND aspect-fits the capture, so the two dimensions
				// can't be steered independently: shrinking both by (1 − W/(2d))
				// reproduces the Mac mid-column and left-edge heights exactly and
				// leaves only the width up to ~7% narrow on the widest cards
				// (which macOS clips at the strip edge anyway).
				double dDip = System.Windows.SystemParameters.PrimaryScreenHeight * EdgePerspectiveDistanceRatio;
				double squeeze = 1.0 - cardW / (2.0 * dDip);
				window.PreviewWidth = cardW * squeeze;
				window.PreviewHeight = cardH * squeeze;

				System.Diagnostics.Debug.WriteLine($"[ThumbnailScale] Scene '{Title}' – Window '{window.Title}' source={pxW}x{pxH}px => card={window.PreviewWidth:F1}x{window.PreviewHeight:F1}dip (s={s:F4})");
			}
		}

		private static double GetPixelsPerDip()
		{
			double pxH = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 0;
			double dipH = System.Windows.SystemParameters.PrimaryScreenHeight;
			return pxH > 0 && dipH > 0 ? pxH / dipH : 1.0;
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

		private double _tiltTopDegrees;
		/// <summary>
		/// Angle (degrees, positive = right end rises) of the live thumbnail's TOP
		/// edge, computed from the tile's on-screen position by
		/// MainWindow.AssignRowTilts. Bound to CompositionThumbnail.TopEdgeDegrees.
		/// </summary>
		public double TiltTopDegrees
		{
			get => _tiltTopDegrees;
			set
			{
				if (_tiltTopDegrees != value)
				{
					_tiltTopDegrees = value;
					RaisePropertyChanged();
				}
			}
		}

		private double _tiltBottomDegrees;
		/// <summary>
		/// Angle (degrees, positive = right end rises) of the live thumbnail's BOTTOM
		/// edge. Bound to CompositionThumbnail.BottomEdgeDegrees.
		/// </summary>
		public double TiltBottomDegrees
		{
			get => _tiltBottomDegrees;
			set
			{
				if (_tiltBottomDegrees != value)
				{
					_tiltBottomDegrees = value;
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
