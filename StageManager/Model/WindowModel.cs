using StageManager.Native;
using StageManager.Native.Window;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StageManager.Model
{
	[System.Diagnostics.DebuggerDisplay("{Title}")]
	public class WindowModel : INotifyPropertyChanged
	{
		//If you get 'dllimport unknown'-, then add 'using System.Runtime.InteropServices;'
		[DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool DeleteObject([In] IntPtr hObject);

		private IWindow _window = null!;
		private ImageSource? _iconSource;

		public event PropertyChangedEventHandler? PropertyChanged;

		public WindowModel(IWindow window)
		{
			Window = window ?? throw new ArgumentNullException(nameof(window));
		}

		private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(memberName));
		}

		public string Title => _window.Title.Length > 20 ? _window.Title.Substring(0, 17) + " ..." : _window.Title;

		public ImageSource? ImageSourceFromBitmap(System.Drawing.Bitmap bmp)
		{
			if (bmp is null)
				return null;

			var handle = bmp.GetHbitmap();
			try
			{
				return Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			}
			finally { DeleteObject(handle); }
		}

		public static ImageSource? IconToImageSource(System.Drawing.Icon? icon)
		{
			if (icon is null)
				return null;

			var imageSource = Imaging.CreateBitmapSourceFromHIcon(
				icon.Handle,
				Int32Rect.Empty,
				BitmapSizeOptions.FromEmptyOptions());

			imageSource.Freeze();
			return imageSource;
		}

		public ImageSource? Icon
		{
			get
			{
				if (_iconSource != null)
					return _iconSource;

				using var icon = ((WindowsWindow)Window).ExtractIcon();
				return _iconSource = IconToImageSource(icon);
			}
		}

		// Scaled dimensions for the DWM thumbnail preview. These are updated by the owning SceneModel
		// whenever the window collection is (re)evaluated so that the preview keeps the correct aspect
		// ratio relative to other windows in the same scene.
		private double _previewWidth = 120; // default fallback
		private double _previewHeight = 90; // default fallback

		public double PreviewWidth
		{
			get => _previewWidth;
			set
			{
				if (Math.Abs(_previewWidth - value) > 0.1)
				{
					_previewWidth = value;
					RaisePropertyChanged();
				}
			}
		}

		public double PreviewHeight
		{
			get => _previewHeight;
			set
			{
				if (Math.Abs(_previewHeight - value) > 0.1)
				{
					_previewHeight = value;
					RaisePropertyChanged();
				}
			}
		}

		public IWindow Window
		{
			get => _window;
			set
			{
				_window = value;

				RaisePropertyChanged();
				RaisePropertyChanged(nameof(Title));
				RaisePropertyChanged(nameof(Handle));
			}
		}

		public IntPtr Handle => _window?.Handle ?? IntPtr.Zero;
	}
}
