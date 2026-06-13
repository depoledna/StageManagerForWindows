using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StageManager.Composition;

namespace StageManager.Controls
{
	/// <summary>
	/// Window thumbnail control backed by
	/// Windows.Graphics.Capture + Windows.UI.Composition. Public surface
	/// is identical: a single <see cref="PreviewHandle"/> dependency
	/// property; layout (Width/Height/Margin) is honoured by WPF.
	/// </summary>
	public partial class CompositionThumbnail : UserControl
	{
		/// <summary>
		/// Resting vertical-shear skew of tray thumbnails, in degrees. Single
		/// source of truth: bound by XAML (x:Static) and reused by the scene-
		/// switch animator so the flying placeholder matches the tray skew.
		/// </summary>
		public const double TrayTiltDegrees = 2.0;

		/// <summary>
		/// Constant 3D perspective for every resting tile (Apple Stage Manager look).
		/// The inner sprite is given a NATIVE vertical-axis rotation of
		/// PerspectiveRotateYDegrees (see CaptureSession.SetSpriteRotationY) so its
		/// RIGHT edge recedes; the container's transform adds a perspective divide
		/// (M34 = -1/PerspectiveDepthPx) that foreshortens that rotation into a
		/// trapezoid. Smaller depth = stronger. Negate the angle if the LEFT edge
		/// recedes instead of the right.
		/// </summary>
		public const double PerspectiveRotateYDegrees = 18.0;
		public const double PerspectiveDepthPx = 220.0;

		private CompositionHost? _compositionHost;
		private D3DDeviceHolder? _devices;
		private CaptureSession? _session;
		private EventHandler? _deviceLostHandler;
		// Base = the visible "at rest" rect (matches WPF Width/Height).
		// Hwnd = the oversized HWND that gives MirrorScale headroom so the
		// rounded clip on the inner SpriteVisual never escapes the HWND rect.
		private double _lastBasePixelWidth;
		private double _lastBasePixelHeight;
		private double _lastHwndPixelWidth;
		private double _lastHwndPixelHeight;
		private double _lastAppliedRadiusPx = double.NaN;
		private Matrix4x4 _lastAppliedTransform = Matrix4x4.Identity;
		private float _lastAppliedOpacity = 1f;
		// While true the live capture visual is on loan to the sidebar drag
		// ghost, which drives the shared session's size/transform directly.
		// The tile must stop touching the session or the two fight over
		// _rootContainer's TransformMatrix every time a bound DP ticks.
		private bool _borrowed;

		// Each side. Total HWND inflation = 1 + 2 * HoverHeadroom. 30% per side
		// covers worst-case lateral overflow at peak hover (scale 1.08 + pull)
		// plus the 3D-tilt near-edge enlargement. Shared: LiveCardHost sizes the
		// borrowed drag/fly card with the same headroom so the host rect matches
		// the tile's and the skewed edge isn't clipped differently at handoff.
		internal const double HoverHeadroom = 0.30;

		public CompositionThumbnail()
		{
			InitializeComponent();
			Loaded += OnLoaded;
			Unloaded += OnUnloaded;
			IsVisibleChanged += OnIsVisibleChanged;
			SizeChanged += OnSizeChanged;
		}

		public static readonly DependencyProperty PreviewHandleProperty = DependencyProperty.Register(
			nameof(PreviewHandle),
			typeof(IntPtr),
			typeof(CompositionThumbnail),
			new PropertyMetadata(IntPtr.Zero));

		public IntPtr PreviewHandle
		{
			get { return (IntPtr)GetValue(PreviewHandleProperty); }
			set { SetValue(PreviewHandleProperty, value); }
		}

		public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
			nameof(CornerRadius),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(0.0, OnCornerRadiusChanged));

		public double CornerRadius
		{
			get => (double)GetValue(CornerRadiusProperty);
			set => SetValue(CornerRadiusProperty, value);
		}

		private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is CompositionThumbnail ct)
				ct.ApplyCornerRadius();
		}

		/// <summary>
		/// Vertical-shear skew of the thumbnail in degrees (Apple Stage Manager
		/// look): y' = y + tan(angle)*x, so sides stay vertical and top/bottom
		/// tilt in parallel; 0 = flat. Composes with hover scale + cursor pull.
		/// </summary>
		public static readonly DependencyProperty SkewAngleDegreesProperty = DependencyProperty.Register(
			nameof(SkewAngleDegrees),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(0.0, OnTransformInputChanged));

		public double SkewAngleDegrees
		{
			get => (double)GetValue(SkewAngleDegreesProperty);
			set => SetValue(SkewAngleDegreesProperty, value);
		}

		// Mirrors the WPF ancestor's animated ScaleX/Y onto the SpriteVisual
		// because HwndHost child windows ignore WPF RenderTransform.
		public static readonly DependencyProperty MirrorScaleProperty = DependencyProperty.Register(
			nameof(MirrorScale),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(1.0, OnTransformInputChanged));

		public double MirrorScale
		{
			get => (double)GetValue(MirrorScaleProperty);
			set => SetValue(MirrorScaleProperty, value);
		}

		// Mirrors the WPF ancestor's animated TranslateTransform.X/Y onto the
		// SpriteVisual. Values are in DIPs; ApplyTransform converts to pixels.
		public static readonly DependencyProperty MirrorTranslateXProperty = DependencyProperty.Register(
			nameof(MirrorTranslateX),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(0.0, OnTransformInputChanged));

		public double MirrorTranslateX
		{
			get => (double)GetValue(MirrorTranslateXProperty);
			set => SetValue(MirrorTranslateXProperty, value);
		}

		public static readonly DependencyProperty MirrorTranslateYProperty = DependencyProperty.Register(
			nameof(MirrorTranslateY),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(0.0, OnTransformInputChanged));

		public double MirrorTranslateY
		{
			get => (double)GetValue(MirrorTranslateYProperty);
			set => SetValue(MirrorTranslateYProperty, value);
		}

		// Mirrors the WPF ancestor's animated Opacity onto the SpriteVisual.
		public static readonly DependencyProperty MirrorOpacityProperty = DependencyProperty.Register(
			nameof(MirrorOpacity),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(1.0, OnMirrorOpacityChanged));

		public double MirrorOpacity
		{
			get => (double)GetValue(MirrorOpacityProperty);
			set => SetValue(MirrorOpacityProperty, value);
		}

		private static void OnTransformInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is CompositionThumbnail ct)
				ct.ApplyTransform();
		}

		private static void OnMirrorOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is CompositionThumbnail ct)
				ct.ApplyOpacity();
		}

		protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
		{
			base.OnPropertyChanged(e);

			if (e.Property == PreviewHandleProperty)
			{
				if ((IntPtr)e.OldValue == IntPtr.Zero && (IntPtr)e.NewValue != IntPtr.Zero)
					StartCaptureIfReady();
				else if ((IntPtr)e.NewValue == IntPtr.Zero && _session is not null)
					TeardownSession();
			}
		}

		// IsVisible isn't a regular DP — changes don't always route through
		// OnPropertyChanged. Subscribe to the dedicated event.
		private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			// While lent to a fly animation the borrower owns the session's
			// run state; ignore tile-visibility flips (the switch sets the tile
			// invisible mid-flight and would otherwise pause the live frame).
			if (_borrowed) return;
			var nowVisible = (bool)e.NewValue;
			if (!nowVisible)
				_session?.Pause();
			else if (_session is not null)
				_session.Resume();
			else if (PreviewHandle != IntPtr.Zero)
				StartCaptureIfReady();
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (_compositionHost is not null) return;

			_compositionHost = new CompositionHost();
			HostContainer.Children.Add(_compositionHost);

			var compositor = _compositionHost.Compositor;
			_devices = D3DDeviceHolder.GetOrCreate(compositor);
			_deviceLostHandler = OnDeviceLost;
			_devices.DeviceLost += _deviceLostHandler;

			if (PreviewHandle != IntPtr.Zero && IsVisible)
				StartCaptureIfReady();
		}

		private void OnUnloaded(object sender, RoutedEventArgs e)
		{
			TeardownSession();

			if (_devices is not null && _deviceLostHandler is not null)
				_devices.DeviceLost -= _deviceLostHandler;
			_deviceLostHandler = null;
			_devices = null;

			if (_compositionHost is not null)
			{
				try { HostContainer.Children.Remove(_compositionHost); }
				catch (Exception ex) { Log.Info("COMPTHUMB", $"HostContainer.Remove threw: {ex.Message}"); }
				try { _compositionHost.Dispose(); }
				catch (Exception ex) { Log.Info("COMPTHUMB", $"CompositionHost.Dispose threw: {ex.Message}"); }
				_compositionHost = null;
			}
		}

		private void StartCaptureIfReady()
		{
			if (_compositionHost is null || _devices is null) return;
			if (_session is not null) return;
			if (PreviewHandle == IntPtr.Zero) return;

			_session = new CaptureSession(PreviewHandle, _compositionHost.Compositor, _devices);
			_session.TargetClosed += OnTargetClosed;
			_session.Start();
			_compositionHost.Root = _session.RootVisual;

			RecomputePixelSize();
			ApplyCornerRadius();
			ApplyTransform();
			ApplyOpacity();

			Log.Info("COMPTHUMB", $"Started session for 0x{PreviewHandle:X}");
		}

		private void OnSizeChanged(object sender, SizeChangedEventArgs e)
		{
			RecomputePixelSize();
			ApplyCornerRadius();
			ApplyTransform();
		}

		protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
		{
			base.OnDpiChanged(oldDpi, newDpi);
			RecomputePixelSize();
			ApplyCornerRadius();
			ApplyTransform();
		}

		private void RecomputePixelSize()
		{
			if (_session is null || _borrowed) return;
			var dpi = VisualTreeHelper.GetDpi(this);
			var baseW = ActualWidth * dpi.DpiScaleX;
			var baseH = ActualHeight * dpi.DpiScaleY;
			var hwndW = baseW * (1.0 + 2.0 * HoverHeadroom);
			var hwndH = baseH * (1.0 + 2.0 * HoverHeadroom);

			// Drive HostContainer's WPF layout size so the underlying HwndHost
			// reserves the oversized HWND. HorizontalAlignment=Center +
			// explicit Width/Height in XAML cause WPF to position it centered
			// inside the UserControl, overflowing equally on each side.
			HostContainer.Width = ActualWidth * (1.0 + 2.0 * HoverHeadroom);
			HostContainer.Height = ActualHeight * (1.0 + 2.0 * HoverHeadroom);

			if (baseW == _lastBasePixelWidth && baseH == _lastBasePixelHeight &&
				hwndW == _lastHwndPixelWidth && hwndH == _lastHwndPixelHeight) return;

			_lastBasePixelWidth = baseW;
			_lastBasePixelHeight = baseH;
			_lastHwndPixelWidth = hwndW;
			_lastHwndPixelHeight = hwndH;
			// Size changed → cached radius / transform are tied to the old size.
			_lastAppliedRadiusPx = double.NaN;
			_lastAppliedTransform = new Matrix4x4(float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
			_session.SetVisualSize(
				new Vector2((float)hwndW, (float)hwndH),
				new Vector2((float)baseW, (float)baseH));
			// Native vertical-axis rotation on the inner sprite — the depth the
			// container's perspective matrix foreshortens into the resting trapezoid.
			_session.SetSpriteRotationY((float)PerspectiveRotateYDegrees);
			_session.SetPerspective((float)PerspectiveDepthPx);
		}

		private void ApplyCornerRadius()
		{
			if (_session is null || _borrowed) return;
			var dpi = VisualTreeHelper.GetDpi(this);
			var pixelRadius = CornerRadius * dpi.DpiScaleX;
			if (pixelRadius == _lastAppliedRadiusPx) return;
			_lastAppliedRadiusPx = pixelRadius;
			// Clip lives on the inner sprite (base pixel size), not the
			// oversized HWND-sized container.
			_session.SetCornerRadius(
				(float)pixelRadius,
				new Vector2((float)_lastBasePixelWidth, (float)_lastBasePixelHeight));
		}

		// Combined skew + scale transform around the visual center. Mirrors the
		// WPF ancestor's animated ScaleTransform — HwndHost children ignore
		// WPF RenderTransform, so the SpriteVisual.TransformMatrix has to do it.
		private void ApplyTransform()
		{
			if (_session is null || _borrowed) return;
			// Transform applied to the HWND-sized container — center is HWND/2,
			// which coincides with the inner sprite's center (sprite is
			// centered inside the container).
			var dpi = VisualTreeHelper.GetDpi(this);
			var translateXPx = MirrorTranslateX * dpi.DpiScaleX;
			var translateYPx = MirrorTranslateY * dpi.DpiScaleY;
			var matrix = ComposeTransform(SkewAngleDegrees, MirrorScale, translateXPx, translateYPx, _lastHwndPixelWidth, _lastHwndPixelHeight);
			if (matrix == _lastAppliedTransform) return;
			_lastAppliedTransform = matrix;
			_session.SetTransformMatrix(matrix);
		}

		private void ApplyOpacity()
		{
			if (_session is null || _borrowed) return;
			var op = (float)Math.Clamp(MirrorOpacity, 0.0, 1.0);
			if (op == _lastAppliedOpacity) return;
			_lastAppliedOpacity = op;
			_session.SetOpacity(op);
		}

		internal static Matrix4x4 ComposeTransform(double angleDegrees, double scale, double translateXPx, double translateYPx, double pixelWidth, double pixelHeight)
		{
			var s = (float)scale;
			var tx = (float)translateXPx;
			var ty = (float)translateYPx;

			// No identity early-out: the perspective below is applied to EVERY tile,
			// so even a flat middle row (angle 0, scale 1, no pull) gets the trapezoid.
			var cx = (float)(pixelWidth / 2.0);
			var cy = (float)(pixelHeight / 2.0);

			var inner = Matrix4x4.CreateScale(s, s, 1f);
			if (angleDegrees != 0.0)
			{
				// Per-row 2D vertical shear (the signed component): y' = y + tan(t)*x.
				// Sides stay ~vertical; +angle drops the right edge (lean down),
				// -angle raises it (lean up), 0 = flat. The angle is supplied per
				// tile via the SkewAngleDegrees DP (set by row position in the tray).
				var rad = (float)(angleDegrees * Math.PI / 180.0);
				var shear = Matrix4x4.Identity;
				shear.M12 = (float)Math.Tan(rad);
				inner = inner * shear;
			}

			// Perspective and Y-rotation are NOT in this matrix — they live on
			// separate visuals in CaptureSession (pure perspective on the root,
			// native rotation on the sprite; see SetPerspective / SetSpriteRotationY),
			// because Composition only foreshortens when perspective sits alone on an
			// ancestor of the rotated visual. This matrix is affine only: scale +
			// per-row shear + cursor pull.

			// Center → transform → re-center, then bias re-center by translate.
			var t1 = Matrix4x4.CreateTranslation(-cx, -cy, 0);
			var t2 = Matrix4x4.CreateTranslation(cx + tx, cy + ty, 0);
			return t1 * inner * t2;
		}

		/// <summary>
		/// The live capture session backing this tile, or null before it starts.
		/// Exposed so the sidebar drag ghost can drive size/skew on the shared
		/// visual while it is borrowed.
		/// </summary>
		internal CaptureSession? Session => _session;

		/// <summary>
		/// Lend the live composition visual to the drag ghost: detach it from this
		/// tile's host and stop driving the session. Returns null (caller falls back
		/// to a static placeholder) if no session is running yet.
		/// </summary>
		internal Windows.UI.Composition.Visual? BorrowRootVisual()
		{
			if (_session is null || _compositionHost is null) return null;
			var visual = _session.RootVisual;
			if (visual is null) return null;
			_borrowed = true;
			// Keep frames flowing even if the tile was paused (hidden) — the
			// flying card must be live, not a frozen last frame.
			_session.Resume();
			_compositionHost.Root = null;
			return visual;
		}

		/// <summary>
		/// Reclaim the visual after the drag ends and re-apply this tile's own
		/// size / corner / transform / opacity (the ghost left the shared session
		/// sized and skewed for the drag, so force past the value-cache guards).
		/// </summary>
		internal void ReturnRootVisual()
		{
			_borrowed = false;
			if (_compositionHost is null || _session is null) return;
			_compositionHost.Root = _session.RootVisual;
			_lastBasePixelWidth = 0;
			_lastBasePixelHeight = 0;
			_lastHwndPixelWidth = 0;
			_lastHwndPixelHeight = 0;
			_lastAppliedRadiusPx = double.NaN;
			_lastAppliedTransform = new Matrix4x4(float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
			_lastAppliedOpacity = float.NaN;
			RecomputePixelSize();
			ApplyCornerRadius();
			ApplyTransform();
			ApplyOpacity();
			// Match the tile's own visibility: if it's hidden in the tray, the
			// session should idle again now that the borrower is done.
			if (!IsVisible) _session.Pause();
		}

		private void TeardownSession()
		{
			if (_session is null) return;
			_session.TargetClosed -= OnTargetClosed;

			if (_compositionHost is not null)
				_compositionHost.Root = null;

			try { _session.Dispose(); }
			catch (Exception ex) { Log.Info("COMPTHUMB", $"Session.Dispose threw: {ex.Message}"); }
			_session = null;
			_lastBasePixelWidth = 0;
			_lastBasePixelHeight = 0;
			_lastHwndPixelWidth = 0;
			_lastHwndPixelHeight = 0;
			_lastAppliedRadiusPx = double.NaN;
			_lastAppliedTransform = new Matrix4x4(float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
			_lastAppliedOpacity = float.NaN;
		}

		private void OnTargetClosed(object? sender, EventArgs e)
		{
			// TargetClosed fires off-thread; marshal teardown to UI to keep
			// CompositionHost mutations on the dispatcher that owns it.
			Dispatcher.BeginInvoke(new Action(() =>
			{
				if (_compositionHost is not null)
					_compositionHost.Root = null;
				TeardownSession();
			}));
		}

		private void OnDeviceLost(object? sender, EventArgs e)
		{
			Dispatcher.BeginInvoke(new Action(() =>
			{
				if (PreviewHandle == IntPtr.Zero) return;
				Log.Info("COMPTHUMB", $"Device lost — restarting session for 0x{PreviewHandle:X}");
				TeardownSession();
				if (IsVisible)
					StartCaptureIfReady();
			}));
		}
	}
}
